# 原生依赖闭合验收

工具只创建独立的验收项目和文件目录。MySQL 模式只允许 `pdm_closure_20261025_` 加 10 位十六进制后缀的隔离库，拒绝生产库名称。

编译时使用 `-p:ServerDirectory=<已冻结候选DLL目录>`，避免把其他工作区改动编入验收版本。

参数顺序：`源目录清单.json 输出目录 八个历史缺档审计.json 原生逐文件依赖.json`，可接 `--mysql 仓库绝对路径 隔离库名`；必须另传 `--verified-body 已通过原生固化检查的PSU文件` 和 `--native-evidence 原生检查结果.json`。

覆盖历史补齐、原版本及原文不变、受控取回、重复提交、失败回滚、固化文件凭证、发布源选择与项目复制。原生几何另用 `Pdm.NativeIsolationAcceptance` 验证。
