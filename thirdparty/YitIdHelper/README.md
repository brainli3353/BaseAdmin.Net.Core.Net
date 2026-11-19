# YitIdHelper(源码移植,2026-08-25)

- 来源:https://github.com/yitter/IdGenerator C# 实现(MIT License,Copyright (c) 2021-2025 yitter)
- 用途:雪花 ID 增强版生成器(对齐 baseadmin 的 YitIdHelper 用法;基准时间/位段可配、时钟回拨处理)
- 决定:用户拍板不依赖 NuGet 包,源码直接移植进本仓库(9 个库文件,~1000 行)
- 使用:并入 Core 项目(M1 任务书引用),命名空间 Yitter.IdGenerator 保留
- 修改记录:未修改(保持上游原样);若修改,本文件记差异
