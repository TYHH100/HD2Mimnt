// CLI 管道协议已下沉到 Core.IPC（HD2Mimnt.Core.IPC.CliPipeProtocol）：AOT 薄客户端与托管服务端
// 共用同一份帧定义。本文件只保留旧类型名的别名，Application 内现有引用无需改动；
// 新帧类型（CliPipeProgress/CliPipePrompt 等）请直接 using HD2Mimnt.Core.IPC。
global using CliProtocol = HD2Mimnt.Core.IPC.CliPipeProtocol;
global using CliPipePacket = HD2Mimnt.Core.IPC.CliPipePacket;
global using CliPipeResult = HD2Mimnt.Core.IPC.CliPipeResult;
