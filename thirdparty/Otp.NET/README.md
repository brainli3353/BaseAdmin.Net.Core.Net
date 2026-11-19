# Otp.NET(源码移植,2026-08-25)

- 来源:https://github.com/kspearrin/Otp.NET v1.4.1(MIT License,Copyright (c) 2017 Kyle Spearrin)
- 用途:TOTP/HOTP 两阶段登录验证码(对齐 baseadmin 的 Otp.NET 用法)
- 决定:用户拍板不依赖 NuGet 包,源码直接移植进本仓库(共 14 文件,~1600 行)
- 使用:并入 Core 项目(命名空间 OtpNet 保留),M1/M2 任务书引用
- 修改记录:未修改(保持上游原样);若修改,本文件记差异
