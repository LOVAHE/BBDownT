using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using QRCoder;
using BBDownT.Core;

namespace BBDownT;

internal static class BBDownTIntlLoginUtil
{
    public static async Task<int> RunAsync(bool importCookie)
    {
        try
        {
            if (!importCookie) AuthenticatedWebProfileStore.Configure(Program.APP_DIR);
            using var client = importCookie ? null : new IntlLoginClient();
            return await RunWithDependenciesAsync(importCookie, client,
                cookie => IntlCookieStore.SaveAsync(Program.APP_DIR, cookie), ReadHiddenCookie,
                Console.WriteLine, BBDownTLoginUtil.PrintQrCode, Task.Delay);
        }
        catch (Exception error)
        {
            Logger.LogDebug("国际站登录 stage=Initialize exception={0}", IntlLoginClient.ExceptionCategory(error));
            Console.WriteLine("国际站登录初始化失败，请重试");
            return 1;
        }
    }

    internal static async Task<int> RunWithDependenciesAsync(bool importCookie, IntlLoginClient? client,
        Func<string, Task> saveCookie, Func<string?> readCookie, Action<string> write,
        Action<Uri> showQrCode, Func<TimeSpan, Task> delay, int maxPolls = 150,
        Action<string>? diagnostic = null)
    {
        diagnostic ??= message => Logger.LogDebug("{0}", message);
        var stage = importCookie ? IntlLoginStage.Import : IntlLoginStage.Generate;
        try
        {
            if (importCookie)
            {
                write("请输入国际站Cookie（输入隐藏，空输入取消）：");
                var input = readCookie();
                if (string.IsNullOrWhiteSpace(input)) { write("已取消导入国际站Cookie"); return 1; }
                var cookie = IntlCookieStore.Normalize(input);
                stage = IntlLoginStage.Save;
                diagnostic("国际站登录 stage=Save begin");
                await saveCookie(cookie);
                write("已保存国际站Cookie，-intl将自动使用");
                return 0;
            }

            if (client is null) throw new InvalidOperationException();
            write("获取国际站登录二维码...");
            var qrCode = await client.GenerateAsync();
            showQrCode(qrCode.Url);
            write("请使用国际站APP扫描二维码并确认登录");
            IntlQrPollResult? pollResult = null;
            stage = IntlLoginStage.Poll;
            var status = await BBDownTLoginUtil.WaitForQrLoginAsync(async () =>
            {
                stage = IntlLoginStage.Poll;
                pollResult = await client.PollAsync(qrCode.Ticket);
                return pollResult.Status;
            }, delay, TimeSpan.FromSeconds(2), () => write("扫码成功，请在国际站APP确认登录"), maxPolls);
            if (status == QrLoginStatus.Expired)
            {
                write("国际站二维码已过期，请重新执行loginintl");
                return 1;
            }
            if (status != QrLoginStatus.Success)
            {
                write("国际站扫码登录超时，请重新执行loginintl");
                return 1;
            }
            stage = IntlLoginStage.Sync;
            var loginCookie = await client.CompleteAsync(pollResult!.GoUrl);
            stage = IntlLoginStage.Save;
            diagnostic("国际站登录 stage=Save begin");
            await saveCookie(loginCookie);
            write("国际站登录成功，-intl将自动使用");
            return 0;
        }
        catch (Exception error)
        {
            // Responses and exception details may contain tickets or cookies.
            var failedStage = stage == IntlLoginStage.Sync && client is not null ? client.CurrentStage : stage;
            diagnostic($"国际站登录 stage={failedStage} exception={IntlLoginClient.ExceptionCategory(error)}");
            var description = failedStage switch
            {
                IntlLoginStage.Import => "导入 Cookie",
                IntlLoginStage.Generate => "生成登录二维码",
                IntlLoginStage.Poll => "轮询扫码状态",
                IntlLoginStage.Sync => "同步登录状态",
                IntlLoginStage.Verify => "验证登录状态",
                IntlLoginStage.Save => "保存登录文件",
                _ => "初始化登录"
            };
            write($"国际站{description}失败，现有登录文件未被替换，请重试或手动导入 Cookie");
            return 1;
        }
    }

    internal static string[] CreateQrRows(QRCodeData data)
        => BBDownTLoginUtil.CreateQrRows(data);

    private static string? ReadHiddenCookie()
    {
        if (Console.IsInputRedirected) return ReadRedirectedCookie(Console.In);
        var previousControlCMode = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
            var cookie = ReadCookieKeys(() => Console.ReadKey(intercept: true));
            Console.WriteLine();
            return cookie;
        }
        finally { Console.TreatControlCAsInput = previousControlCMode; }
    }

    internal static string? ReadCookieKeys(Func<ConsoleKeyInfo> readKey)
    {
        var input = new StringBuilder();
        while (true)
        {
            var key = readKey();
            if (key.Key == ConsoleKey.Enter) return input.ToString();
            var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
            if (key.Key == ConsoleKey.Escape || (control && key.Key is ConsoleKey.C or ConsoleKey.D)) return null;
            if (control && key.Key == ConsoleKey.U) { input.Clear(); continue; }
            if (key.Key == ConsoleKey.Backspace) { if (input.Length > 0) input.Length--; continue; }
            if (!char.IsControl(key.KeyChar))
            {
                if (input.Length >= 16384) return null;
                input.Append(key.KeyChar);
            }
        }
    }

    internal static string? ReadRedirectedCookie(TextReader reader)
    {
        var input = new StringBuilder();
        while (true)
        {
            var next = reader.Read();
            if (next == -1) break;
            if (next == '\n') return input.ToString().TrimEnd('\r');
            if (input.Length >= 16384) return null;
            input.Append((char)next);
        }
        return input.Length == 0 ? null : input.ToString().TrimEnd('\r');
    }
}
