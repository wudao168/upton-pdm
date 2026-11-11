namespace Upton.Pdm.Application;

/// <summary>Editable starting templates for equipment projects, not an industry-mandated remuneration standard.</summary>
public static class ProjectBonusDefaults
{
    public static ProjectBonusSettings Create()
    {
        var roles = new List<BonusRoleTemplate>();
        void Role(string id, string name, decimal share, string department, string[] shipment, string[] acceptance)
        {
            foreach (var phase in new[] { "Shipment", "Acceptance" })
            {
                var items = phase == "Shipment" ? shipment : acceptance;
                var weights = new decimal[] { 30, 25, 20, 15, 10 };
                roles.Add(new(id + "-" + phase, name, phase, share, items.Select((item, i) =>
                    new BonusIndicator(id + "-" + phase + "-" + i, item, 100, weights[i],
                        "90–100：证据完整、质量稳定且主动预防问题；75–89：满足约定，少量问题按期闭环；60–74：存在可归责返工或遗漏；0–59：重大缺项或未完成。按实际证据评分，不因缺失记录自动给满分。同一事件不得重复扣分。",
                        "提供对应任务、评审、检验、问题关闭或交付记录编号，说明本人责任及经批准的范围变化。",
                        [new(department, 70), new("quality", 30)])).ToArray()));
            }
        }
        Role("pm", "项目经理", 20, "division",
            ["里程碑计划与资源协调", "变更审批与风险闭环", "跨部门问题推进", "发货条件与资料完整性", "复盘及客户沟通"],
            ["终验收组织与目标达成", "遗留事项与变更闭环", "现场资源协调", "验收资料与移交", "项目复盘与知识沉淀"]);
        Role("mechanical", "机械设计", 18, "technical",
            ["设计评审与可制造性", "BOM及图纸准确性", "装配问题整改质量", "标准化与可维护性", "设计资料齐套交付"],
            ["机械性能与验收达成", "现场设计问题闭环", "最终BOM与图纸一致性", "可靠性与维护改进", "设计经验沉淀"]);
        Role("electrical", "电气设计", 15, "technical",
            ["电气方案与设计质量", "电气BOM及接线准确性", "安全联锁验证", "装配调试支持", "电气图纸与备份交付"],
            ["电气性能与稳定性", "现场电气问题闭环", "安全联锁复验", "竣工图与实物一致性", "操作维护资料移交"]);
        Role("software", "软件与控制", 15, "technical",
            ["控制功能与节拍达成", "异常恢复与程序质量", "接口联调与版本管理", "安全逻辑验证", "程序备份与操作说明"],
            ["连续运行与验收性能", "现场软件缺陷闭环", "版本冻结与可追溯性", "客户培训与操作体验", "程序资产与技术移交"]);
        Role("procurement", "采购与供应链", 10, "procurement",
            ["采购齐套与履约质量", "来料异常与供应商闭环", "变更响应与替代料合规", "采购单据准确完整", "供应资源风险预警"],
            ["现场补料及服务响应", "供应商质量问题闭环", "退换补订记录完整性", "采购结算资料齐套", "供应商绩效与经验反馈"]);
        Role("assembly", "装配制造", 12, "manufacturing",
            ["装配一次交验质量", "工艺执行与自检", "返工责任及问题闭环", "现场规范与设备保护", "装配记录及移交"],
            ["最终装配质量与可靠性", "现场整改完成质量", "工艺变更及返修追溯", "客户现场施工规范", "竣工记录与维护移交"]);
        Role("commissioning", "调试与现场服务", 15, "manufacturing",
            ["功能与节拍验证", "调试问题关闭质量", "安全检查与异常验证", "试运行记录完整性", "发货交接与操作培训"],
            ["终验收指标与稳定运行", "客户问题关闭质量", "现场安全与作业规范", "客户培训与使用支持", "验收记录与服务移交"]);
        // Quality is assessed independently by management and technical departments.
        Role("quality", "质量与检验", 8, "division",
            ["检验覆盖与证据完整性", "不合格项闭环有效性", "发货质量确认", "检验标准执行一致性", "风险预防与质量反馈"],
            ["验收质量证据完整性", "整改复验有效性", "客户质量问题分析", "最终交付质量确认", "质量复盘与改进跟踪"]);
        roles = roles.Select(r => r.Id.StartsWith("quality-") ? r with { Indicators = r.Indicators.Select(i => i with { Departments = [new("division", 60), new("technical", 40)] }).ToArray() } : r).ToList();
        return new(0,
            [new("division", "事业部负责人"), new("finance", "财务"), new("quality", "质量"), new("technical", "技术"),
             new("manufacturing", "制造"), new("procurement", "采购"), new("assistant", "总经理助理"), new("general", "总经理")], roles,
            [new("milestone", "节点控制", 100, 60, "以冻结基线与批准延期为准：全部按期100；可归责延期≤3天90；4–7天75；8–14天60；超过14天40。客户原因及批准变更提供记录后扣除影响，不重复扣减个人同一事项。", "基线版本、计划/实际日期、延期审批、发货单或终验收报告编号。", [new("division", 70), new("manufacturing", 30)]),
             new("budget", "预算控制", 100, 40, "用批准预算对比实际已发生及已承诺未结算成本：不超预算100；超支≤3%90；3–5%75；5–10%60；超过10%40。发货与终验收分别核实成本完整性，批准变更按调整后的预算评价。", "批准预算版本、实际及承诺成本对账、批准变更编号。未到票不等于无成本。", [new("finance", 100)])],
            [new("division", "事业部负责人", ["division"]), new("parallel", "多部门并行审批", ["finance", "quality", "technical", "manufacturing"], true),
             new("assistant", "总经理助理", ["assistant"]), new("general", "总经理", ["general"])]);
    }
}
