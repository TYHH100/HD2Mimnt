using Xunit;

// 样本共享 SQLite 原生池与进程资源，顺序测量才能归因耗时和内存。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
