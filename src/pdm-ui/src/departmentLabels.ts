const emptyDepartmentLabels = new Set(['—', '-', '–', '待分配', '未分配', '未归属', '未设置', '未知部门', '已归档部门', '公司直属', '部门直属', '事业部待分配', '事业部待补充', '部门待补充', '未指定部门', '请选择部门', '原部门已移除，请重新选择'])

export function isDepartmentName(name?: string | null) {
  const label = name?.trim() ?? ''
  return label.length > 0 && !emptyDepartmentLabels.has(label)
}
