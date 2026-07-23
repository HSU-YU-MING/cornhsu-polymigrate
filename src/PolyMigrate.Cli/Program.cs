using PolyMigrate.Cli;

// 進入點只做一件事:把 Ctrl-C 接成合作式取消(讓長時間的 probe/fetch 能乾淨中止),
// 其餘全交給可測的 Cli.RunAsync。exit code 契約見 Cli。
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // 不讓 runtime 直接砍掉,改走合作式取消
    cts.Cancel();
};

return await Cli.RunAsync(args, cts.Token);
