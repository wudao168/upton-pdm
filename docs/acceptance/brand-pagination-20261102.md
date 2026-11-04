# 品牌库搜索与分页验收

本机版本：V2026.11.02.142013。界面发布，未生成安装包。

- 使用全局 SearchField 和 Element Plus 分页控件，默认每页 50 条，可选 50、100、200 条。
- 搜索品牌名称和曾用名称，忽略大小写及首尾空格；搜索、清空和改变每页数量回到第一页。
- 只读人员可以搜索、翻页；维护权限沿用原规则。翻页和查询不清除正在编辑的品牌，保存仍使用原品牌 ID 和版本。
- 分页仅控制品牌库维护列表的渲染，不改变品牌选项接口或物料品牌候选范围。

验证：4 项品牌库单元测试通过，类型检查及界面构建通过。浏览器验证 125 条模拟数据的翻页、100 条切换、曾用名称搜索、空结果、清空及筛选后新增改名。

真实本机 HTTP 页面验证 613 个品牌：默认 50 条，50/100/200 切换正确，搜索 upton 返回 2 条，空结果和清空恢复正确，未修改品牌记录。使用 Playwright（Browser plugin not available）。

HTTP、HTTPS 主界面与审核浮层资源校验通过；服务器与四份客户端 UI 清单一致，每份核对 56 项引用资源。本机实际 MainWindow、审核 WebView2、五种主题及延迟状态同步验证通过。

部署回执：`.local/brand-pagination-deployment.json`。客户端验证：`.local/brand-pagination-native-result.json`。本会话 visualizations 目录保存 `brand-pagination-live-proof.json` 和 `brand-pagination-live.png`。
