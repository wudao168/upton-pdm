# 存档归属确认按钮字体 — 2026-11-01

候选2026.11.01.203000，基线为已部署2026.11.01.200000。

用户要求“3个按钮字体放大一倍”。仅在ProjectAdmissionConfirmationDialog.AdmissionButton构造中指定Microsoft YaHei UI 18磅，原继承字体为9磅。适用于“确认/已确认”“返回修改”“确认归属”。按钮仍为150×60，保留现有颜色、确认门禁和点击行为。

已对照[SOLIDWORKS PDM检入说明](https://help.solidworks.com/2025/english/EnterprisePDM/FileExplorer/t_Checking_in_Files.htm?id=a992cad2def14649bf9dd87cef0608e4)。本次为UPLM自身的展示调整，不涉及该文档规定的文件选择、版本或引用规则，也不声称18磅来自PDM标准。

基于200000部署源码冻结构建，仅一份产品源码有差异。编译0错误、0警告，PDB176份源码匹配。隔离渲染560×420、780×700窗口的确认前后四种状态，12个按钮记录均为18磅、完整显示且位于窗口内；已目视核对渲染图。现有12项存档界面流程检查通过。

候选DLL SHA256：72509CECB16A69CF1ACF6AC53058D06AE5B6E51806426F079267198838226779。

字体调整已合并到循环引用修复2026.11.01.220000，并于2026-11-01 21:26完成本机部署。三处插件文件版本核对通过；不再单独发布203000，以免覆盖后续修复。未中断运行中的业务操作。

证据：tmp/archive-admission-font-20261101，包括visual/render.json、四张渲染图、session-checks.log、pdb-audit.json及fix-evidence.json。未生成安装包。
