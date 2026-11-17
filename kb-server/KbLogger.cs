using System.Text;

namespace KbMcp;

/// <summary>
/// 精简文件日志。
/// </summary>
/// <remarks>
/// 日志写入 %KB_ROOT%\.kb\logs\kb-server-yyyyMMdd.log；
/// 因 MCP 的 stdout 只能输出协议报文，所有诊断信息一律落文件。
/// </remarks>
public static class KbLogger
{
    private static readonly object SyncRoot = new();
    private static readonly string LogDirectory = ResolveLogDirectory();

    /// <summary>
    /// 解析日志目录，优先使用 KB_ROOT，未配置时退到临时目录。
    /// </summary>
    /// <returns>
    /// 日志目录绝对路径。
    /// </returns>
    private static string ResolveLogDirectory()
    {
        try
        {
            // 优先跟随知识库根目录，方便与数据一起排查
            var root = Environment.GetEnvironmentVariable("KB_ROOT");
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root, ".kb", "logs");
            }

            // 未配置时落到临时目录，避免写权限问题
            return Path.Combine(Path.GetTempPath(), "kb-server-logs");
        }
        catch (Exception)
        {
            // 解析目录失败不应影响主流程
            return Path.GetTempPath();
        }
    }

    /// <summary>
    /// 记录普通信息日志。
    /// </summary>
    /// <param name="message">日志内容。</param>
    public static void Info(string message)
    {
        Write("INFO", message);
    }

    /// <summary>
    /// 记录异常日志。
    /// </summary>
    /// <param name="message">日志内容。</param>
    /// <param name="error">异常对象，可为空。</param>
    public static void Error(string message, Exception? error = null)
    {
        // 异常信息与方法上下文一起记录，便于回溯
        var detail = error is null ? message : $"{message} | {error.GetType().Name}: {error.Message} | {error.StackTrace}";
        Write("ERROR", detail);
    }

    /// <summary>
    /// 写日志到当天的文件。
    /// </summary>
    /// <param name="level">日志级别。</param>
    /// <param name="message">日志内容。</param>
    private static void Write(string level, string message)
    {
        try
        {
            // 加锁避免多线程写同一文件时内容交错
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"kb-server-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(file, line, new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // 日志失败不能影响服务本身，直接吞掉
        }
    }
}
