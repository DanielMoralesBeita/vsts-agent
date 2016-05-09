using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker.CodeCoverage
{
    public sealed class CodeCoverageCommands : AgentService, ICommandExtension
    {
        private int _buildId;
        // publish code coverage inputs
        private string _codeCoverageTool;
        private string _summaryFileLocation;
        private string _reportDirectory;
        private List<string> _additionalCodeCoverageFiles;

        public void ProcessCommand(IExecutionContext context, Command command)
        {
            if (string.Equals(command.Event, WellKnownResultsCommand.PublishCodeCoverage, StringComparison.OrdinalIgnoreCase))
            {
                ProcessPublishCodeCoverageCommand(context, command.Properties);
            }
            else
            {
                throw new Exception(StringUtil.Loc("CodeCoverageCommandNotFound", command.Event));
            }
        }

        public Type ExtensionType
        {
            get
            {
                return typeof(ICommandExtension);
            }
        }

        public string CommandArea
        {
            get
            {
                return "codecoverage";
            }
        }

        #region publish code coverage helper methods
        private void ProcessPublishCodeCoverageCommand(IExecutionContext context, Dictionary<string, string> eventProperties)
        {
            ArgUtil.NotNull(context, nameof(context));

            _buildId = context.Variables.Build_BuildId ?? -1;
            if (!IsHostTypeBuild(context) || _buildId < 0)
            {
                //In case the publishing codecoverage is not applicable for current Host type we continue without publishing
                context.Warning(StringUtil.Loc("CodeCoveragePublishIsValidOnlyForBuild"));
                return;
            }
            
            LoadPublishCodeCoverageInputs(eventProperties);

            string project = context.Variables.System_TeamProject;

            long? containerId = context.Variables.Build_ContainerId;
            ArgUtil.NotNull(containerId, nameof(containerId));

            Guid projectId = context.Variables.System_TeamProjectId ?? Guid.Empty;
            ArgUtil.NotEmpty(projectId, nameof(projectId));

            //step 1: read code coverage summary
            var reader = GetCodeCoverageSummaryReader(_codeCoverageTool);
            context.Output(StringUtil.Loc("ReadingCodeCoverageSummary", _summaryFileLocation));
            var coverageData = reader.GetCodeCoverageSummary(context, _summaryFileLocation);

            if (coverageData == null || coverageData.Count() == 0)
            {
                context.Warning(StringUtil.Loc("CodeCoverageDataIsNull"));
            }

            Client.VssConnection connection = WorkerUtilies.GetVssConnection(context);
            var codeCoveragePublisher = HostContext.GetService<ICodeCoveragePublisher>();
            codeCoveragePublisher.InitializePublisher(_buildId, connection);

            var commandContext = HostContext.CreateService<IAsyncCommandContext>();
            commandContext.InitializeCommandContext(context, StringUtil.Loc("PublishCodeCoverage"));
            commandContext.Task = PublishCodeCoverageAsync(context, commandContext, codeCoveragePublisher, coverageData, project, projectId, containerId.Value, context.CancellationToken);
            context.AsyncCommands.Add(commandContext);
        }

        private async Task PublishCodeCoverageAsync(IExecutionContext executionContext, IAsyncCommandContext commandContext, ICodeCoveragePublisher codeCoveragePublisher, IEnumerable<CodeCoverageStatistics> coverageData,
                                                    string project, Guid projectId, long containerId, CancellationToken cancellationToken)
        {
            //step 2: publish code coverage summary to TFS
            if (coverageData != null && coverageData.Count() > 0)
            {
                commandContext.Output(StringUtil.Loc("PublishingCodeCoverage"));
                foreach (var coverage in coverageData)
                {
                    commandContext.Output(StringUtil.Format(" {0}- {1} of {2} covered.", coverage.Label, coverage.Covered, coverage.Total));
                }
                await codeCoveragePublisher.PublishCodeCoverageSummaryAsync(coverageData, project, cancellationToken);
            }

            // step 3: publish code coverage files as build artifacts

            string additionalCodeCoverageFilePath = null;
            string destinationSummaryFile = null;
            var newReportDirectory = _reportDirectory;
            try
            {
                var filesToPublish = new List<Tuple<string, string>>();

                if (!Directory.Exists(newReportDirectory))
                {
                    if (!string.IsNullOrWhiteSpace(newReportDirectory))
                    {
                        // user gave a invalid report directory. Write warning and continue.
                        executionContext.Warning(StringUtil.Loc("DirectoryNotFound", newReportDirectory));
                    }
                    newReportDirectory = GetCoverageDirectory(_buildId.ToString(), CodeCoverageUtilities.ReportDirectory);
                    Directory.CreateDirectory(newReportDirectory);
                }

                var summaryFileName = Path.GetFileName(_summaryFileLocation);
                destinationSummaryFile = Path.Combine(newReportDirectory, CodeCoverageUtilities.SummaryFileDirectory + _buildId, summaryFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationSummaryFile));
                File.Copy(_summaryFileLocation, destinationSummaryFile, true);


                filesToPublish.Add(new Tuple<string, string>(newReportDirectory, GetCoverageDirectoryName(_buildId.ToString(), CodeCoverageUtilities.ReportDirectory)));

                if (_additionalCodeCoverageFiles != null && _additionalCodeCoverageFiles.Count != 0)
                {
                    additionalCodeCoverageFilePath = GetCoverageDirectory(_buildId.ToString(), CodeCoverageUtilities.RawFilesDirectory);
                    CodeCoverageUtilities.CopyFilesFromFileListWithDirStructure(_additionalCodeCoverageFiles, ref additionalCodeCoverageFilePath);
                    filesToPublish.Add(new Tuple<string, string>(additionalCodeCoverageFilePath, GetCoverageDirectoryName(_buildId.ToString(), CodeCoverageUtilities.RawFilesDirectory)));
                }
                commandContext.Output(StringUtil.Loc("PublishingCodeCoverageFiles"));


                await codeCoveragePublisher.PublishCodeCoverageFilesAsync(commandContext, projectId, containerId, filesToPublish, File.Exists(Path.Combine(newReportDirectory, CodeCoverageUtilities.DefaultIndexFile)), cancellationToken);
            }
            catch (IOException ex)
            {
                executionContext.Warning(StringUtil.Loc("ErrorOccuredWhilePublishingCCFiles", ex.Message));
            }
            finally
            {
                // clean temporary files.
                if (!string.IsNullOrEmpty(additionalCodeCoverageFilePath))
                {
                    if (Directory.Exists(additionalCodeCoverageFilePath))
                    {
                        Directory.Delete(path: additionalCodeCoverageFilePath, recursive: true);
                    }
                }

                if (!string.IsNullOrEmpty(destinationSummaryFile))
                {
                    var summaryFileDirectory = Path.GetDirectoryName(destinationSummaryFile);
                    if (Directory.Exists(summaryFileDirectory))
                    {
                        Directory.Delete(path: summaryFileDirectory, recursive: true);
                    }
                }

                if (!Directory.Exists(_reportDirectory))
                {
                    if (Directory.Exists(newReportDirectory))
                    {
                        //delete the generated report directory
                        Directory.Delete(path: newReportDirectory, recursive: true);
                    }
                }
            }
        }

        private ICodeCoverageSummaryReader GetCodeCoverageSummaryReader(string codeCoverageTool)
        {
            var extensionManager = HostContext.GetService<IExtensionManager>();
            ICodeCoverageSummaryReader summaryReader = (extensionManager.GetExtensions<ICodeCoverageSummaryReader>()).FirstOrDefault(x => codeCoverageTool.Equals(x.Name, StringComparison.OrdinalIgnoreCase));

            if (summaryReader == null)
            {
                throw new ArgumentException(StringUtil.Loc("UnknownCodeCoverageTool", codeCoverageTool));
            }
            return summaryReader;
        }

        private bool IsHostTypeBuild(IExecutionContext context)
        {
            var hostType = context.Variables.System_HostType;

            if (hostType != null && String.Equals(hostType.ToString(), "build", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private void LoadPublishCodeCoverageInputs(Dictionary<string, string> eventProperties)
        {
            //validate codecoverage tool input
            eventProperties.TryGetValue(PublishCodeCoverageEventProperties.CodeCoverageTool, out _codeCoverageTool);
            if (string.IsNullOrEmpty(_codeCoverageTool))
            {
                throw new ArgumentException(StringUtil.Loc("ArgumentNeeded", "CodeCoverageTool"));
            }

            //validate summary file input
            eventProperties.TryGetValue(PublishCodeCoverageEventProperties.SummaryFile, out _summaryFileLocation);
            if (string.IsNullOrEmpty(_summaryFileLocation))
            {
                throw new ArgumentException(StringUtil.Loc("ArgumentNeeded", "SummaryFile"));
            }

            eventProperties.TryGetValue(PublishCodeCoverageEventProperties.ReportDirectory, out _reportDirectory);

            string additionalFilesInput;
            eventProperties.TryGetValue(PublishCodeCoverageEventProperties.AdditionalCodeCoverageFiles, out additionalFilesInput);
            if (!string.IsNullOrEmpty(additionalFilesInput) && additionalFilesInput.Split(',').Count() > 0)
            {
                _additionalCodeCoverageFiles = additionalFilesInput.Split(',').ToList<string>();
            }
        }

        private string GetCoverageDirectory(string buildId, string directoryName)
        {
            return Path.Combine(Path.GetTempPath(), GetCoverageDirectoryName(buildId, directoryName));
        }

        private string GetCoverageDirectoryName(string buildId, string directoryName)
        {
            return directoryName + "_" + buildId;
        }
        #endregion

        internal static class WellKnownResultsCommand
        {
            internal static readonly string PublishCodeCoverage = "publish";
        }

        internal static class PublishCodeCoverageEventProperties
        {
            internal static readonly string CodeCoverageTool = "codecoveragetool";
            internal static readonly string SummaryFile = "summaryfile";
            internal static readonly string ReportDirectory = "reportdirectory";
            internal static readonly string AdditionalCodeCoverageFiles = "additionalcodecoveragefiles";
        }
    }
}