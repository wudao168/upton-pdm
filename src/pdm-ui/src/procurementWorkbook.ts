import { strToU8, zipSync } from 'three/examples/jsm/libs/fflate.module.js'

type Cell = string | number
const spreadsheetNamespace = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'

function xml(value: string) {
  return value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

export function createProcurementWorkbook(headers: string[], rows: Cell[][]): Uint8Array {
  const sheetRows = [headers, ...rows].map((row, index) => `<row r="${index + 1}">${row.map(value =>
    typeof value === 'number' && Number.isFinite(value)
      ? `<c><v>${value}</v></c>`
      : `<c t="inlineStr"><is><t xml:space="preserve">${xml(String(value))}</t></is></c>`,
  ).join('')}</row>`).join('')
  const files: Record<string, string> = {
    '[Content_Types].xml': '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
    '_rels/.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    'xl/workbook.xml': `<workbook xmlns="${spreadsheetNamespace}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="采购跟踪" sheetId="1" r:id="rId1"/></sheets></workbook>`,
    'xl/_rels/workbook.xml.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
    'xl/worksheets/sheet1.xml': `<worksheet xmlns="${spreadsheetNamespace}"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>${headers.map((_, i) => `<col min="${i + 1}" max="${i + 1}" width="20" customWidth="1"/>`).join('')}</cols><sheetData>${sheetRows}</sheetData></worksheet>`,
  }
  return zipSync(Object.fromEntries(Object.entries(files).map(([name, content]) => [name, new Uint8Array(strToU8(content))])))
}

export function downloadProcurementWorkbook(filename: string, headers: string[], rows: Cell[][]) {
  const bytes = createProcurementWorkbook(headers, rows)
  const url = URL.createObjectURL(new Blob([new Uint8Array(bytes)], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }))
  const link = document.createElement('a')
  link.href = url
  link.download = filename.replace(/[\\/:*?"<>|]/g, '_')
  document.body.append(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}
