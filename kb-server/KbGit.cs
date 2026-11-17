using System.Diagnostics;
using System.Text;

namespace KbMcp;

/// <summary>
/// Git 提交辅助类。
/// </summary>
/// <remarks>
/// 每次写入知识库后自动提交，保证可 diff、可回滚；Git 失败只记录日志，不影响工具返回。
/// </remarks>
public static class KbGit
{
    /// <summary>
    /// 提交指定文件到知识库仓库。
    /// </summary>
    /// <param name="root">仓库根目录。</param>
    /// <param name="files">本次改动的文件绝对路径集合。</param>
    /// <param name="message">提交信息。</param>
    /// <returns>
    /// 是否提交成功。
    /// </returns>
    public static bool Commit(string root, IEnumerable<string> files, string message)
    {
        try
        {
            // 先加入暂存区，再提交；文件较多时逐个 add 避免命令行过长
            foreach (var file in files)
            {
                Run(root, $"add -- \"{file}\"");
            }

            var committed = Run(root, $"commit -m \"{message.Replace("\"", "'")}\"");
            if (committed)
            {
                KbLogger.Info($"git commit 成功: {message}");
            }
            else
            {
                KbLogger.Info($"git commit 跳过（可能无变更）: {message}");
            }

            return committed;
        }
        catch (Exception ex)
        {
            // 无 git 或仓库异常时降级为纯文件写入
            KbLogger.Error("git commit 失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 执行单条 git 命令。
    /// </summary>
    /// <param name="root">仓库根目录。</param>
    /// <param name="arguments">git 子命令与参数。</param>
    /// <returns>
    /// 退出码为 0 时返回 true。
    /// </returns>
    private static bool Run(string root, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            // 记录命令与输出，便于排查提交失败原因
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                KbLogger.Error($"git 进程启动失败: {arguments}");
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            if (process.ExitCode != 0)
            {
                KbLogger.Error($"git {arguments} 退出码 {process.ExitCode}: {output} {error}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            KbLogger.Error($"git {arguments} 执行异常", ex);
            return false;
        }
    }
}
