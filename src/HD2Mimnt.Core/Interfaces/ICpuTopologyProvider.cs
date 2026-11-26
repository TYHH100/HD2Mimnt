using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>读取机器 CPU 拓扑；实现可以缓存，但每次模拟器启动后必须重新识别。</summary>
public interface ICpuTopologyProvider
{
    CpuTopology GetTopology();
}