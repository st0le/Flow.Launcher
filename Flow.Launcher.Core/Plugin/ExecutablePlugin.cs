using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Core.Plugin
{
    internal class ExecutablePlugin : JsonRPCPlugin
    {
        private readonly ProcessStartInfo _startInfo;
        private readonly int argIndex;
        public override string SupportedLanguage { get; set; } = AllowedLanguage.Executable;

        public ExecutablePlugin(string filename, string arguments)
        {
            _startInfo = new ProcessStartInfo
            {
                FileName = filename,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (string.IsNullOrEmpty(arguments))
            {
                // add arguments to Executable
                // For eg. FileName = powershell.exe, Arguments = -NoProfile -File pluginscript.ps1
                _startInfo.ArgumentList.Add(arguments);
                argIndex = 1;
            }
            else
            {
                argIndex = 0;
            }

            // required initialisation for below request calls 
            _startInfo.ArgumentList.Add(string.Empty);
        }

        protected override Task<Stream> RequestAsync(JsonRPCRequestModel request, CancellationToken token = default)
        {
            // since this is not static, request strings will build up in ArgumentList if index is not specified
            _startInfo.ArgumentList[argIndex] = request.ToString();
            return ExecuteAsync(_startInfo, token);
        }

        protected override string Request(JsonRPCRequestModel rpcRequest, CancellationToken token = default)
        {
            // since this is not static, request strings will build up in ArgumentList if index is not specified
            _startInfo.ArgumentList[argIndex] = rpcRequest.ToString();
            return Execute(_startInfo);
        }
    }
}
