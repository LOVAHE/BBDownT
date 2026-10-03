using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using QRCoder;

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
                Console.WriteLine, ShowQrCode, Task.Delay);
        }
        catch (Exception)
        {
            Console.WriteLine("国际站登录初始化失败，请重试");
            return 1;
        }
    }

    internal static async Task<int> RunWithDependenciesAsync(bool importCookie, IntlLoginClient? client,
        Func<string, Task> saveCookie, Func<string?> readCookie, Action<string> write,
        Action<Uri> showQrCode, Func<TimeSpan, Task> delay, int maxPolls = 150)
    {
        try
        {
            if (importCookie)
            {
                write("请输入国际站Cookie（输入隐藏，空输入取消）：");
                var input = readCookie();
                if (string.IsNullOrWhiteSpace(input)) { write("已取消导入国际站Cookie"); return 1; }
                await saveCookie(IntlCookieStore.Normalize(input));
                write("已保存国际站Cookie，-intl将自动使用");
                return 0;
            }

            if (client is null) throw new InvalidOperationException();
            write("获取国际站登录二维码...");
            var qrCode = await client.GenerateAsync();
            showQrCode(qrCode.Url);
            write("请使用国际站APP扫描二维码并确认登录");
            var scannedMessageShown = false;
            for (var poll = 0; poll < maxPolls; poll++)
            {
                await delay(TimeSpan.FromSeconds(2));
                var result = await client.PollAsync(qrCode.Ticket);
                switch (result.Status)
                {
                    case IntlQrStatus.Waiting: break;
                    case IntlQrStatus.Scanned:
                        if (!scannedMessageShown) { write("扫码成功，请在国际站APP确认登录"); scannedMessageShown = true; }
                        break;
                    case IntlQrStatus.Expired:
                        write("国际站二维码已过期，请重新执行loginintl");
                        return 1;
                    case IntlQrStatus.Success:
                        var cookie = await client.CompleteAsync(result.GoUrl);
                        await saveCookie(cookie);
                        write("国际站登录成功，-intl将自动使用");
                        return 0;
                }
            }
            write("国际站扫码登录超时，请重新执行loginintl");
            return 1;
        }
        catch (Exception)
        {
            // Responses and exception details may contain tickets or cookies.
            write("国际站登录或保存失败，现有登录文件未被替换，请重试");
            return 1;
        }
    }

    private static void ShowQrCode(Uri url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url.AbsoluteUri, QRCodeGenerator.ECCLevel.Q);
        // Two vertical pixels per character keep the ticket QR within the
        // width of a typical terminal without wrapping and corrupting it.
        try
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.White;
            foreach (var row in CreateQrRows(data)) Console.WriteLine(row);
        }
        finally { Console.ResetColor(); }
    }

    internal static string[] CreateQrRows(QRCodeData data)
    {
        var matrix = data.ModuleMatrix;
        var rows = new string[(matrix.Count + 1) / 2];
        for (var y = 0; y < matrix.Count; y += 2)
        {
            var row = new char[matrix.Count];
            for (var x = 0; x < matrix.Count; x++)
            {
                var top = matrix[y][x];
                var bottom = y + 1 < matrix.Count && matrix[y + 1][x];
                row[x] = top ? bottom ? '█' : '▀' : bottom ? '▄' : ' ';
            }
            rows[y / 2] = new string(row);
        }
        return rows;
    }

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
