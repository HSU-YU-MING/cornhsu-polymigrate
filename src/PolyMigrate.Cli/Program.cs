using PolyMigrate.Cli;

// 進入點只做一件事:把 Ctrl-C 接成合作式取消(每個指令都看這個 token,乾淨收尾後回 130),
// 其餘全交給可測的 Cli.RunAsync。exit code 契約見 Cli。
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // 第一次 Ctrl-C:合作式取消——指令在下一個檢查點(下一頁、下一張圖、下一個請求)停下,
    // 經 Cli.Report() 回 130。
    // 第二次:不再攔截(e.Cancel 維持 false)→ 讓 runtime 直接終止。給「單一步驟本身就很久」
    // (例如一個 40 秒逾時的請求、一張超大圖)時的逃生門;那時結束碼由作業系統決定,不是 130。
    if (cts.IsCancellationRequested)
    {
        return;
    }
    e.Cancel = true;
    try
    {
        cts.Cancel();
    }
    catch (ObjectDisposedException)
    {
        // 工作已結束、cts 已釋放,行程正要退出——忽略
    }
};

return await Cli.RunAsync(args, cts.Token);
