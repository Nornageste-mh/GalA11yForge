using Xunit;

// E2E 测试会启动进程、构建 CLI、写临时游戏副本，禁止并行以免互相争用 dotnet 输出目录与 BepInEx 缓存。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
