export const u9UnitOptions = [
  { code: '001', name: '个' },
  { code: '002', name: '台' },
  { code: '003', name: '个' },
  { code: '004', name: '盒' },
  { code: '005', name: '卷' },
  { code: '006', name: '捆' },
  { code: '007', name: '双' },
  { code: '008', name: '片' },
  { code: '009', name: '桶' },
  { code: '010', name: '支' },
  { code: '011', name: '组' },
  { code: '012', name: '箱' },
  { code: '013', name: '包' },
] as const

export function u9UnitLabel(code: string) {
  const unit = u9UnitOptions.find(item => item.code === code)
  return unit ? `${unit.code} ${unit.name}` : code
}

export function u9UnitName(code: string) {
  return u9UnitOptions.find(item => item.code === code)?.name ?? code
}
