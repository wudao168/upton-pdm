import { strToU8, zipSync } from 'three/examples/jsm/libs/fflate.module.js'
import type { MaterialCategory } from './types'

const ns = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
export const materialImportHeaders = ['U9C分类编码', '物料名称', '计量单位', '规格型号', '材质', '品牌', '表面处理', '重量', '重量单位', '备注', '采购链接', '选型建议', '参考价格', '3D链接', '资料链接', '优先推荐']
const unitOptions = ['个', '台', '盒', '卷', '捆', '双', '片', '桶', '支', '组', '箱', '包']

function xml(value: string) {
  return value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

function columnName(index: number) {
  let value = index + 1
  let result = ''
  while (value > 0) { value--; result = String.fromCharCode(65 + value % 26) + result; value = Math.floor(value / 26) }
  return result
}

function cell(reference: string, value: string, style = 0) {
  return `<c r="${reference}" s="${style}" t="inlineStr"><is><t xml:space="preserve">${xml(value)}</t></is></c>`
}

export function createMaterialImportTemplate(categories: MaterialCategory[]): Uint8Array {
  const allowedCategories = categories.filter(item => item.isActive && item.isVisible && item.allowCreate && item.pdmKind)
  const headerRow = `<row r="1" ht="30" customHeight="1">${materialImportHeaders.map((header, index) => cell(`${columnName(index)}1`, header, 1)).join('')}</row>`
  const dataRows = Array.from({ length: 1000 }, (_, index) => `<row r="${index + 2}">${cell(`C${index + 2}`, '个', 2)}</row>`).join('')
  const categoryLast = Math.max(2, allowedCategories.length + 1)
  const unitLast = unitOptions.length + 1
  const files: Record<string, string> = {
    '[Content_Types].xml': '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>',
    '_rels/.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    'xl/workbook.xml': `<workbook xmlns="${ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="料品导入" sheetId="1" r:id="rId1"/><sheet name="字典" sheetId="2" state="hidden" r:id="rId2"/></sheets></workbook>`,
    'xl/_rels/workbook.xml.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>',
    'xl/styles.xml': `<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="${ns}"><fonts count="2"><font><sz val="11"/><name val="Microsoft YaHei"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Microsoft YaHei"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF159A8C"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="2"><border/><border><left style="thin"><color rgb="FFD7DEE9"/></left><right style="thin"><color rgb="FFD7DEE9"/></right><top style="thin"><color rgb="FFD7DEE9"/></top><bottom style="thin"><color rgb="FFD7DEE9"/></bottom></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>`,
    'xl/worksheets/sheet1.xml': `<worksheet xmlns="${ns}"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>${materialImportHeaders.map((_, index) => `<col min="${index + 1}" max="${index + 1}" width="${index === 1 || index === 3 || index === 9 || index === 11 ? 24 : 16}" customWidth="1"/>`).join('')}</cols><sheetData>${headerRow}${dataRows}</sheetData><autoFilter ref="A1:P1001"/><dataValidations count="3"><dataValidation type="list" allowBlank="0" showErrorMessage="1" errorTitle="分类无效" error="请选择模板提供的U9C分类" sqref="A2:A1001"><formula1>'字典'!$A$2:$A$${categoryLast}</formula1></dataValidation><dataValidation type="list" allowBlank="0" showErrorMessage="1" errorTitle="单位无效" error="请选择模板提供的计量单位" sqref="C2:C1001"><formula1>'字典'!$C$2:$C$${unitLast}</formula1></dataValidation><dataValidation type="list" allowBlank="1" sqref="P2:P1001"><formula1>"是,否"</formula1></dataValidation></dataValidations></worksheet>`,
    'xl/worksheets/sheet2.xml': `<worksheet xmlns="${ns}"><sheetData><row r="1">${cell('A1', 'U9C分类编码')}${cell('C1', '计量单位')}</row>${Array.from({ length: Math.max(allowedCategories.length, unitOptions.length) }, (_, index) => `<row r="${index + 2}">${allowedCategories[index] ? cell(`A${index + 2}`, `${allowedCategories[index].code} ${allowedCategories[index].name}`) : ''}${unitOptions[index] ? cell(`C${index + 2}`, unitOptions[index]) : ''}</row>`).join('')}</sheetData></worksheet>`,
  }
  return zipSync(Object.fromEntries(Object.entries(files).map(([name, content]) => [name, new Uint8Array(strToU8(content))])))
}

export function downloadMaterialImportTemplate(categories: MaterialCategory[]) {
  const bytes = createMaterialImportTemplate(categories)
  const url = URL.createObjectURL(new Blob([new Uint8Array(bytes)], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }))
  const link = document.createElement('a')
  link.href = url
  link.download = '料品批量导入模板.xlsx'
  document.body.append(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}
