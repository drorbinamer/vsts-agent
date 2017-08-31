using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public class ExecutionContextType
    {
        public static string Job = "Job";
        public static string Task = "Task";
    }

    [ServiceLocator(Default = typeof(ExecutionContext))]
    public interface IExecutionContext : IAgentService
    {
        TaskResult? Result { get; set; }
        TaskResult? CommandResult { get; set; }
        CancellationToken CancellationToken { get; }
        List<ServiceEndpoint> Endpoints { get; }
        List<SecureFile> SecureFiles { get; }
        PlanFeatures Features { get; }
        Variables Variables { get; }
        Variables TaskVariables { get; }
        HashSet<string> OutputVariables { get; }
        List<IAsyncCommandContext> AsyncCommands { get; }
        List<string> PrependPath { get; }
        ContainerInfo Container { get; }

        // Initialize
        void InitializeJob(JobRequestMessage message, CancellationToken token);
        void CancelToken();
        IExecutionContext CreateChild(Guid recordId, string displayName, string refName, Variables taskVariables = null);

        // logging
        bool WriteDebug { get; }
        void Write(string tag, string message);
        void QueueAttachFile(string type, string name, string filePath);

        // timeline record update methods
        void Start(string currentOperation = null);
        TaskResult Complete(TaskResult? result = null, string currentOperation = null);
        void SetVariable(string name, string value, bool isSecret, bool isOutput);
        void SetTimeout(TimeSpan? timeout);
        void AddIssue(Issue issue);
        void Progress(int percentage, string currentOperation = null);
        void UpdateDetailTimelineRecord(TimelineRecord record);
    }

    public sealed class ExecutionContext : AgentService, IExecutionContext
    {
        private const int _maxIssueCount = 10;

        // TODO: Does this represent the most recently inserted timeline record?
        private readonly TimelineRecord _record = new TimelineRecord();

        private readonly Dictionary<Guid, TimelineRecord> _detailRecords = new Dictionary<Guid, TimelineRecord>();
        private readonly object _loggerLock = new object();
        private readonly List<IAsyncCommandContext> _asyncCommands = new List<IAsyncCommandContext>();
        private readonly HashSet<string> _outputvariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IPagingLogger _logger;
        private ISecretMasker _secretMasker;
        private IJobServerQueue _jobServerQueue;
        private IExecutionContext _parentExecutionContext;

        // Debug Timeline Information
        private Guid _debugTimelineId;
        private Guid _debugTimelineRecordId;        

        private Guid _mainTimelineId;
        private Guid _detailTimelineId;
        private int _childTimelineRecordOrder = 0;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _throttlingReported = false;

        // only job level ExecutionContext will track throttling delay.
        private long _totalThrottlingDelayInMilliseconds = 0;

        // Store the child ExecutionContexts
        private readonly List<ExecutionContext> _childExecutionContexts = new List<ExecutionContext>();

        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        public List<ServiceEndpoint> Endpoints { get; private set; }
        public List<SecureFile> SecureFiles { get; private set; }
        public Variables Variables { get; private set; }
        public Variables TaskVariables { get; private set; }
        public HashSet<string> OutputVariables => _outputvariables;
        public bool WriteDebug { get; private set; }
        public List<string> PrependPath { get; private set; }
        public ContainerInfo Container { get; private set; }

        // Whether or not this is a Job ExecutionContext. The alternative is to be a Task.
        public bool IsJob { get; private set; }

        public List<IAsyncCommandContext> AsyncCommands => _asyncCommands;

        public TaskResult? Result
        {
            get
            {
                return _record.Result;
            }
            set
            {
                _record.Result = value;
            }
        }

        public TaskResult? CommandResult { get; set; }

        private string ContextType => _record.RecordType;

        // might remove this.
        // TODO: figure out how do we actually use the result code.
        public string ResultCode
        {
            get
            {
                return _record.ResultCode;
            }
            set
            {
                _record.ResultCode = value;
            }
        }

        public PlanFeatures Features { get; private set; }

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);

            _jobServerQueue = HostContext.GetService<IJobServerQueue>();
            _secretMasker = HostContext.GetService<ISecretMasker>();
        }

        public void CancelToken()
        {
            _cancellationTokenSource.Cancel();
        }

        public IExecutionContext CreateChild(Guid recordId, string displayName, string refName, Variables taskVariables = null)
        {
            Trace.Entering();

            var child = new ExecutionContext();
            child.Initialize(HostContext);
            child.Features = Features;
            child.Variables = Variables;
            child.Endpoints = Endpoints;
            child.SecureFiles = SecureFiles;
            child.TaskVariables = taskVariables;
            child._cancellationTokenSource = new CancellationTokenSource();
            child.WriteDebug = WriteDebug;
            child._parentExecutionContext = this;
            child.PrependPath = PrependPath;
            child.Container = Container;

            // Setup the time line for courtesy debug logging. These should be created on demand.
            // This on demand creation will happen in Complete(...)
            Guid debugTimelineRecordId = Guid.NewGuid();

            child._debugTimelineId = _mainTimelineId;
            child._debugTimelineRecordId = debugTimelineRecordId;
            child._displayName = displayName;
            child._refName = refName;

            child.InitializeTimelineRecord(_mainTimelineId, recordId, _record.Id, ExecutionContextType.Task, displayName, refName, ++_childTimelineRecordOrder);

            child._logger = HostContext.CreateService<IPagingLogger>();
            child._logger.Setup(_mainTimelineId, recordId, performCourtesyDebugLogging: !WriteDebug, debugTimelineId: _mainTimelineId, debugTimelineRecordId: debugTimelineRecordId);
            child.IsJob = false;

            _childExecutionContexts.Add(child);

            return child;
        }

        public void Start(string currentOperation = null)
        {
            _record.CurrentOperation = currentOperation ?? _record.CurrentOperation;
            _record.StartTime = DateTime.UtcNow;
            _record.State = TimelineRecordState.InProgress;

            _jobServerQueue.QueueTimelineRecordUpdate(_mainTimelineId, _record);
        }

        public TaskResult Complete(TaskResult? result = null, string currentOperation = null)
        {
            if (result != null)
            {
                Result = result;
            }

            // report total delay caused by server throttling.
            if (_totalThrottlingDelayInMilliseconds > 0)
            {
                this.Warning(StringUtil.Loc("TotalThrottlingDelay", TimeSpan.FromMilliseconds(_totalThrottlingDelayInMilliseconds).TotalSeconds));
            }

            _record.CurrentOperation = currentOperation ?? _record.CurrentOperation;
            _record.FinishTime = DateTime.UtcNow;
            _record.PercentComplete = 100;
            _record.Result = _record.Result ?? TaskResult.Succeeded;
            _record.State = TimelineRecordState.Completed;

            _jobServerQueue.QueueTimelineRecordUpdate(_mainTimelineId, _record);

            // complete all detail timeline records.
            if (_detailTimelineId != Guid.Empty && _detailRecords.Count > 0)
            {
                foreach (var record in _detailRecords)
                {
                    record.Value.FinishTime = record.Value.FinishTime ?? DateTime.UtcNow;
                    record.Value.PercentComplete = record.Value.PercentComplete ?? 100;
                    record.Value.Result = record.Value.Result ?? TaskResult.Succeeded;
                    record.Value.State = TimelineRecordState.Completed;

                    _jobServerQueue.QueueTimelineRecordUpdate(_detailTimelineId, record.Value);
                }
            }

            _cancellationTokenSource?.Dispose();

            _logger.End();

            if (result == TaskResult.Failed && IsJob && !WriteDebug)
            {
                ProcessFailedBuild();
            }

            return Result.Value;
        }

        // The job has failed. We want to stop uploading the Task file uploads.
        // We need to be careful here because I think we want to keep uploading if 
        // it's a Job level log but stop if it's Task level? Need to clarify this.
        private void ProcessFailedBuild()
        {
            // TODO: Do we only do this if it's the Job level ExecutionContext?
            // I think so. Then we would also need to create the root Build(Job) and Task timelines
            // That would need to be done before we run the code below this.
            CreateDebugTimelines();

            _jobServerQueue.StopFileUploadQueue();
            _jobServerQueue.StartDebugFileUploadQueue();

        }

        // The job has failed. We now need to create timelines for the Job and
        // all child Tasks.
        private void CreateDebugTimelines()
        {
            // TODO: Does each execution context store the timeline id and timeline record id for
            // debug? we could use this to create the timelines for the root job and all children
            // execution contexts
            InitializeTimelineRecord(
                timelineId: _mainTimelineId, // We want this to be the id of "Build"?
                timelineRecordId: _debugTimelineRecordId, 
                parentTimelineRecordId: _record.Id, // Or is this the id of "Build"?
                recordType: ExecutionContextType.Task, // Does this have to be Task or Job?
                displayName: "Build-DEBUG", 
                refName: "Build-DEBUGRefName", // TODO: Figure out how to name this correctly. Other places use nameof(...) 
                order: ++_childTimelineRecordOrder
            );

            //_logger.FlushDebugLog(_mainTimelineId, diagnosticsTimelineRecordId);

            int diagnosticRecordOrder = 0;
            foreach (ExecutionContext childExecutionContext in _childExecutionContexts)
            {
                Guid childDiagNodeRecordId = childExecutionContext._debugTimelineRecordId;
                String displayName = childExecutionContext._displayName + "_FROMDEBUG";
                String refName = childExecutionContext._refName + "_RefName";

                // create a timeline record attached to the parent Diagnostic node.
                InitializeTimelineRecord(
                    timelineId: _mainTimelineId, // I think this is the id of the Build root timeline?
                    timelineRecordId: childDiagNodeRecordId, 
                    parentTimelineRecordId: _debugTimelineRecordId, 
                    recordType: ExecutionContextType.Task, 
                    displayName: displayName, // TODO: Use the name of the task from the child execution context
                    refName: refName, // same as one above + RefName
                    order: ++diagnosticRecordOrder
                );

                // flush the debug logs
                //childExecutionContext._logger.FlushDebugLog(_mainTimelineId, childDiagNodeRecordId);
            }
        }

        // TODO: Revisit if it's best to have a separate method to InitializeTimelineRecord for this.
        //       Not sure yet.
        private void InitializeConvenienceDebugTimelineRecord()
        {

        }

        public void SetVariable(string name, string value, bool isSecret, bool isOutput)
        {
            ArgUtil.NotNullOrEmpty(name, nameof(name));
            if (isOutput || OutputVariables.Contains(name))
            {
                _record.Variables[name] = new VariableValue()
                {
                    Value = value,
                    IsSecret = isSecret
                };
                _jobServerQueue.QueueTimelineRecordUpdate(_mainTimelineId, _record);

                ArgUtil.NotNullOrEmpty(_record.RefName, nameof(_record.RefName));
                Variables.Set($"{_record.RefName}.{name}", value, secret: isSecret);
            }
            else
            {
                Variables.Set(name, value, secret: isSecret);
            }
        }

        public void SetTimeout(TimeSpan? timeout)
        {
            if (timeout != null)
            {
                _cancellationTokenSource.CancelAfter(timeout.Value);
            }
        }

        public void Progress(int percentage, string currentOperation = null)
        {
            if (percentage > 100 || percentage < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(percentage));
            }

            _record.CurrentOperation = currentOperation ?? _record.CurrentOperation;
            _record.PercentComplete = Math.Max(percentage, _record.PercentComplete.Value);

            _jobServerQueue.QueueTimelineRecordUpdate(_mainTimelineId, _record);
        }

        // This is not thread safe, the caller need to take lock before calling issue()
        public void AddIssue(Issue issue)
        {
            ArgUtil.NotNull(issue, nameof(issue));
            issue.Message = _secretMasker.MaskSecrets(issue.Message);
            if (issue.Type == IssueType.Error)
            {
                if (_record.ErrorCount <= _maxIssueCount)
                {
                    _record.Issues.Add(issue);
                }

                _record.ErrorCount++;
            }
            else if (issue.Type == IssueType.Warning)
            {
                if (_record.WarningCount <= _maxIssueCount)
                {
                    _record.Issues.Add(issue);
                }

                _record.WarningCount++;
            }

            _jobServerQueue.QueueTimelineRecordUpdate(_mainTimelineId, _record);
        }

        public void UpdateDetailTimelineRecord(TimelineRecord record)
        {
            ArgUtil.NotNull(record, nameof(record));

            if (record.RecordType == ExecutionContextType.Job)
            {
                throw new ArgumentOutOfRangeException(nameof(record));
            }

            if (_detailTimelineId == Guid.Empty)
            {
                // create detail timeline
                _detailTimelineId = Guid.NewGuid();
                _record.Details = new Timeline(_detailTimelineId);

                _jobServerQueue.QueueTimelineRecordUpdate(_mainTimelineId, _record);
            }

            TimelineRecord existRecord;
            if (_detailRecords.TryGetValue(record.Id, out existRecord))
            {
                existRecord.Name = record.Name ?? existRecord.Name;
                existRecord.RecordType = record.RecordType ?? existRecord.RecordType;
                existRecord.Order = record.Order ?? existRecord.Order;
                existRecord.ParentId = record.ParentId ?? existRecord.ParentId;
                existRecord.StartTime = record.StartTime ?? existRecord.StartTime;
                existRecord.FinishTime = record.FinishTime ?? existRecord.FinishTime;
                existRecord.PercentComplete = record.PercentComplete ?? existRecord.PercentComplete;
                existRecord.CurrentOperation = record.CurrentOperation ?? existRecord.CurrentOperation;
                existRecord.Result = record.Result ?? existRecord.Result;
                existRecord.ResultCode = record.ResultCode ?? existRecord.ResultCode;
                existRecord.State = record.State ?? existRecord.State;

                _jobServerQueue.QueueTimelineRecordUpdate(_detailTimelineId, existRecord);
            }
            else
            {
                _detailRecords[record.Id] = record;
                _jobServerQueue.QueueTimelineRecordUpdate(_detailTimelineId, record);
            }
        }

        public void InitializeJob(JobRequestMessage message, CancellationToken token)
        {
            // Validation
            Trace.Entering();
            ArgUtil.NotNull(message, nameof(message));
            ArgUtil.NotNull(message.Environment, nameof(message.Environment));
            ArgUtil.NotNull(message.Environment.SystemConnection, nameof(message.Environment.SystemConnection));
            ArgUtil.NotNull(message.Environment.Endpoints, nameof(message.Environment.Endpoints));
            ArgUtil.NotNull(message.Environment.Variables, nameof(message.Environment.Variables));
            ArgUtil.NotNull(message.Plan, nameof(message.Plan));

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);

            // Features
            Features = ApiUtil.GetFeatures(message.Plan);

            // Endpoints
            Endpoints = message.Environment.Endpoints;
            Endpoints.Add(message.Environment.SystemConnection);

            // SecureFiles
            SecureFiles = message.Environment.SecureFiles;

            // Variables (constructor performs initial recursive expansion)
            List<string> warnings;
            Variables = new Variables(HostContext, message.Environment.Variables, message.Environment.MaskHints, out warnings);

            // Prepend Path
            PrependPath = new List<string>();

            // Docker 
            Container = new ContainerInfo()
            {
                ContainerImage = Variables.Get("_PREVIEW_VSTS_DOCKER_IMAGE"),
                ContainerName = $"VSTS_{Variables.System_HostType.ToString()}_{message.JobId.ToString("D")}",
            };

            // Proxy variables
            var agentWebProxy = HostContext.GetService<IVstsAgentWebProxy>();
            if (!string.IsNullOrEmpty(agentWebProxy.ProxyAddress))
            {
                Variables.Set(Constants.Variables.Agent.ProxyUrl, agentWebProxy.ProxyAddress);
                Environment.SetEnvironmentVariable("VSTS_HTTP_PROXY", string.Empty);

                if (!string.IsNullOrEmpty(agentWebProxy.ProxyUsername))
                {
                    Variables.Set(Constants.Variables.Agent.ProxyUsername, agentWebProxy.ProxyUsername);
                    Environment.SetEnvironmentVariable("VSTS_HTTP_PROXY_USERNAME", string.Empty);
                }

                if (!string.IsNullOrEmpty(agentWebProxy.ProxyPassword))
                {
                    Variables.Set(Constants.Variables.Agent.ProxyPassword, agentWebProxy.ProxyPassword, true);
                    Environment.SetEnvironmentVariable("VSTS_HTTP_PROXY_PASSWORD", string.Empty);
                }

                if (agentWebProxy.ProxyBypassList.Count > 0)
                {
                    Variables.Set(Constants.Variables.Agent.ProxyBypassList, JsonUtility.ToString(agentWebProxy.ProxyBypassList));
                }
            }

            // TODO: Are these needed? They map to _mainTimelineId and _record.Id respectively.
            //       But I think those values change over time. I think we want these to be static.
            //       They get passed to the logger and become static there so that is the effect I want here too.
            _debugTimelineId = message.Timeline.Id;
            _debugTimelineRecordId = message.JobId;
            _displayName = message.JobName;
            _refName = message.JobRefName;

            // Job timeline record.
            InitializeTimelineRecord(
                timelineId: message.Timeline.Id,
                timelineRecordId: message.JobId,
                parentTimelineRecordId: null,
                recordType: ExecutionContextType.Job,
                displayName: message.JobName,
                refName: message.JobRefName,
                order: null); // The job timeline record's order is set by server.

            // Verbosity (from system.debug).
            WriteDebug = Variables.System_Debug ?? false;

            // Logger (must be initialized before writing warnings).
            _logger = HostContext.CreateService<IPagingLogger>();
            _logger.Setup(_mainTimelineId, _record.Id, performCourtesyDebugLogging: !WriteDebug, debugTimelineId: _debugTimelineId, debugTimelineRecordId: _debugTimelineRecordId);

            // Log warnings from recursive variable expansion.
            warnings?.ForEach(x => this.Warning(x));

            // Hook up JobServerQueueThrottling event, we will log warning on server tarpit.
            _jobServerQueue.JobServerQueueThrottling += JobServerQueueThrottling_EventReceived;

            // This is a Job ExecutionContext
            IsJob = true;
        }

        private string _displayName;
        private string _refName;

        // Do not add a format string overload. In general, execution context messages are user facing and
        // therefore should be localized. Use the Loc methods from the StringUtil class. The exception to
        // the rule is command messages - which should be crafted using strongly typed wrapper methods.
        public void Write(string tag, string message)
        {
            string msg = _secretMasker.MaskSecrets($"{tag}{message}");

            bool isDebugLogMessage = (tag == WellKnownTags.Debug);

            lock (_loggerLock)
            {
                _logger.Write(msg, isDebugLogMessage);
            }

            // write to job level execution context's log file.
            var parentContext = _parentExecutionContext as ExecutionContext;
            if (parentContext != null)
            {
                lock (parentContext._loggerLock)
                {
                    parentContext._logger.Write(msg, isDebugLogMessage);
                }
            }

            _jobServerQueue.QueueWebConsoleLine(msg);
        }

        public void QueueAttachFile(string type, string name, string filePath)
        {
            ArgUtil.NotNullOrEmpty(type, nameof(type));
            ArgUtil.NotNullOrEmpty(name, nameof(name));
            ArgUtil.NotNullOrEmpty(filePath, nameof(filePath));

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(StringUtil.Loc("AttachFileNotExist", type, name, filePath));
            }

            _jobServerQueue.QueueFileUpload(_mainTimelineId, _record.Id, type, name, filePath, deleteSource: false);
        }

        private void InitializeTimelineRecord(Guid timelineId, Guid timelineRecordId, Guid? parentTimelineRecordId, string recordType, string displayName, string refName, int? order)
        {
            _mainTimelineId = timelineId;
            _record.Id = timelineRecordId;
            _record.RecordType = recordType;
            _record.Name = displayName;
            _record.RefName = refName;
            _record.Order = order;
            _record.PercentComplete = 0;
            _record.State = TimelineRecordState.Pending;
            _record.ErrorCount = 0;
            _record.WarningCount = 0;

            if (parentTimelineRecordId != null && parentTimelineRecordId.Value != Guid.Empty)
            {
                _record.ParentId = parentTimelineRecordId;
            }

            var configuration = HostContext.GetService<IConfigurationStore>();
            _record.WorkerName = configuration.GetSettings().AgentName;

            _jobServerQueue.QueueTimelineRecordUpdate(_mainTimelineId, _record);
        }

        private void JobServerQueueThrottling_EventReceived(object sender, ThrottlingEventArgs data)
        {
            Interlocked.Add(ref _totalThrottlingDelayInMilliseconds, Convert.ToInt64(data.Delay.TotalMilliseconds));

            if (!_throttlingReported)
            {
                this.Warning(StringUtil.Loc("ServerTarpit"));
                _throttlingReported = true;
            }
        }
    }

    // The Error/Warning/etc methods are created as extension methods to simplify unit testing.
    // Otherwise individual overloads would need to be implemented (depending on the unit test).
    public static class ExecutionContextExtension
    {
        public static void Error(this IExecutionContext context, Exception ex)
        {
            context.Error(ex.Message);
            context.Debug(ex.ToString());
        }

        // Do not add a format string overload. See comment on ExecutionContext.Write().
        public static void Error(this IExecutionContext context, string message)
        {
            context.Write(WellKnownTags.Error, message);
            context.AddIssue(new Issue() { Type = IssueType.Error, Message = message });
        }

        // Do not add a format string overload. See comment on ExecutionContext.Write().
        public static void Warning(this IExecutionContext context, string message)
        {
            context.Write(WellKnownTags.Warning, message);
            context.AddIssue(new Issue() { Type = IssueType.Warning, Message = message });
        }

        // Do not add a format string overload. See comment on ExecutionContext.Write().
        public static void Output(this IExecutionContext context, string message)
        {
            context.Write(null, message);
        }

        // Do not add a format string overload. See comment on ExecutionContext.Write().
        public static void Command(this IExecutionContext context, string message)
        {
            context.Write(WellKnownTags.Command, message);
        }

        // Do not add a format string overload. See comment on ExecutionContext.Write().
        public static void Section(this IExecutionContext context, string message)
        {
            context.Write(WellKnownTags.Section, message);
        }

        //
        // Verbose output is enabled by setting System.Debug
        // It's meant to help the end user debug their definitions.
        // Why are my inputs not working?  It's not meant for dev debugging which is diag
        //
        // Do not add a format string overload. See comment on ExecutionContext.Write().
        public static void Debug(this IExecutionContext context, string message)
        {
            context.Write(WellKnownTags.Debug, message);
        }
    }

    public static class WellKnownTags
    {
        public static readonly string Section = "##[section]";
        public static readonly string Command = "##[command]";
        public static readonly string Error = "##[error]";
        public static readonly string Warning = "##[warning]";
        public static readonly string Debug = "##[debug]";
    }
}