using System.Diagnostics;
using PinNote.Core.Updates;

return await UpdaterProgram.RunAsync(args);

internal static class UpdaterProgram
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinNote",
        "update.log");

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            var packagePath = Require(options, "package");
            var manifestPath = Require(options, "manifest");
            var targetDirectory = Require(options, "target");
            var processId = int.Parse(Require(options, "pid"), System.Globalization.CultureInfo.InvariantCulture);

            WriteLog("等待 PinNote 退出。");
            await WaitForExitAsync(processId, TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            var manifestJson = await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false);
            var update = UpdateManifestCodec.ParseAndVerify(manifestJson, UpdateTrust.PublicKeyPem, UpdateTrust.Channel);
            WriteLog($"开始安装 {update.Version}。");
            await UpdateInstaller.InstallAsync(packagePath, targetDirectory, update).ConfigureAwait(false);

            var executable = Path.Combine(UpdateInstaller.EnsureInstallRoot(targetDirectory), "PinNote.exe");
            Process.Start(new ProcessStartInfo(executable, $"--updated-from {update.Version}")
            {
                UseShellExecute = true,
                WorkingDirectory = targetDirectory
            });
            WriteLog($"已安装并启动 {update.Version}。");
            return 0;
        }
        catch (Exception exception)
        {
            WriteLog($"更新失败：{exception}");
            return 1;
        }
    }

    private static async Task WaitForExitAsync(int processId, TimeSpan timeout)
    {
        Process? process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        using (var timeoutSource = new CancellationTokenSource(timeout))
        {
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                throw new TimeoutException("PinNote 未在允许时间内退出。", exception);
            }
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("更新器参数格式无效。");
            }
            options.Add(args[index][2..], args[index + 1]);
        }
        return options;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"缺少更新器参数 --{name}。");

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
            {
                File.Move(LogPath, LogPath + ".old", overwrite: true);
            }
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
