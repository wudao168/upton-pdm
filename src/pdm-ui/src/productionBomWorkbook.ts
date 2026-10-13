import { strToU8, zipSync } from 'three/examples/jsm/libs/fflate.module.js'

const ns = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
const xml = (value: string) => value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '')
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
function column(index: number) {
  let result = ''
  for (let n = index + 1; n > 0; n = Math.floor((n - 1) / 26)) result = String.fromCharCode(65 + (n - 1) % 26) + result
  return result
}

export function createProductionBomWorkbook(headers: string[], rows: (string | number)[][]): Blob {
  const data = [headers, ...rows].map((values, index) => `<row r="${index + 1}">${values.map((value, i) =>
    typeof value === 'number' ? `<c r="${column(i)}${index + 1}"><v>${value}</v></c>`
      : `<c r="${column(i)}${index + 1}" t="inlineStr"><is><t xml:space="preserve">${xml(value)}</t></is></c>`).join('')}</row>`).join('')
  const files = {
    '[Content_Types].xml': '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
    '_rels/.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    'xl/workbook.xml': `<workbook xmlns="${ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="发布BOM" sheetId="1" r:id="rId1"/></sheets></workbook>`,
    'xl/_rels/workbook.xml.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
    'xl/worksheets/sheet1.xml': `<worksheet xmlns="${ns}"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>${headers.map((_, i) => `<col min="${i + 1}" max="${i + 1}" width="20" customWidth="1"/>`).join('')}</cols><sheetData>${data}</sheetData><autoFilter ref="A1:${column(headers.length - 1)}${rows.length + 1}"/></worksheet>`,
  }
  const bytes = zipSync(Object.fromEntries(Object.entries(files).map(([name, value]) => [name, new Uint8Array(strToU8(value))])))
  return new Blob([new Uint8Array(bytes)], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
}
