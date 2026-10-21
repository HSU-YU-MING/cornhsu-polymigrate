using System.Runtime.InteropServices;

namespace PolyMigrate.Cli;

/// <summary>stdout 接到哪裡。</summary>
internal static class StandardOutput
{
    private const int StdOutputHandle = -11;
    private const int FileTypeDisk = 0x0001;

    /// <summary>
    /// Windows 上 stdout 是不是被導到「磁碟上的檔案」(cmd、Git Bash 的 <c>&gt;</c>)。
    ///
    /// <para>只認檔案、不認管線,是刻意的:Windows PowerShell 5.1(與 7.4 之前的 PowerShell)讀原生指令的
    /// 輸出走管線,並依 console 碼頁解讀——那條路上改寫 UTF-8 反而會變成亂碼,而它原本是好的
    /// (docs/contracts.md 記為繞法)。console 本身(沒有導向)也維持 console 編碼,畫面才顯示得對。</para>
    ///
    /// <para>其他平台一律 false:console 編碼取自 locale,一般本來就是 UTF-8。</para>
    /// </summary>
    public static bool IsRedirectedToFileOnWindows()
    {
        if (!OperatingSystem.IsWindows() || !Console.IsOutputRedirected)
        {
            return false;
        }
        var handle = GetStdHandle(StdOutputHandle);
        return handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) == FileTypeDisk;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern int GetFileType(IntPtr hFile);
}
