import { expect, it } from 'vitest'
import { unzipSync, strFromU8 } from 'three/examples/jsm/libs/fflate.module.js'
import { createProcurementWorkbook } from '../src/procurementWorkbook'
import { createProjectPlansWorkbook, projectPlanPdfHtml } from '../src/projectPlanExport'
import type { ProjectExportMetadata } from '../src/exportFormat'
import type { ProjectPlan } from '../src/types'
const meta: ProjectExportMetadata={title:'采购跟踪',company:'测试公司',scope:'当前子项目',versionLabel:'数据版本',version:'V1',exportedAt:'2026-10-14T09:00:00+08:00',exportedBy:'工程师',fields:['主项目号','主项目名称','型号','客户','子项目号','子项目名称','子项目型号','整套数量','事业部','项目经理','主设','工程师'].map((key,i)=>({key,value:i===4?'P700014-1':'实际资料'}))}
it('adds the approved header while preserving literal codes, numbers and filter ranges',()=>{
 const files=unzipSync(createProcurementWorkbook(['物料编码','数量'],[['001234',12.5]],'采购跟踪',meta))
 const sheet=new DOMParser().parseFromString(strFromU8(files['xl/worksheets/sheet1.xml']!),'application/xml')
 expect(sheet.querySelector('parsererror')).toBeNull()
 expect(sheet.querySelector('c[r="A8"]')?.getAttribute('t')).toBe('inlineStr')
 expect(sheet.querySelector('c[r="A8"]')?.textContent).toBe('001234')
 expect(sheet.querySelector('c[r="B8"]')?.textContent).toBe('12.5')
 expect(sheet.querySelector('autoFilter')?.getAttribute('ref')).toBe('A7:B8')
 expect(sheet.querySelector('pane')?.getAttribute('ySplit')).toBe('7')
 expect(files['xl/media/uplm-company-logo.png']?.slice(0,4)).toEqual(new Uint8Array([137,80,78,71]))
})
it('gives every selected child independent sheets and PDF sections with its own header',()=>{
 const plan={tasks:[],stages:[],plannedStart:'2026-10-14',plannedFinish:'2026-10-15',baselineVersion:2,templateName:'计划',approvalStatus:'Approved'} as unknown as ProjectPlan
 const items=[1,2].map(i=>({projectCode:`P700014-${i}`,projectName:`子项目${i}`,plan,metadata:{exportHeader:{...meta,fields:meta.fields.map(f=>f.key==='子项目号'?{...f,value:`P700014-${i}`}:f)}}}))
 const files=unzipSync(createProjectPlansWorkbook(items))
 expect(Object.keys(files).filter(p=>/^xl\/worksheets\/sheet\d+\.xml$/.test(p))).toHaveLength(4)
 expect(strFromU8(files['xl/worksheets/sheet1.xml']!)).toContain('P700014-1')
 expect(strFromU8(files['xl/worksheets/sheet3.xml']!)).toContain('P700014-2')
 expect(projectPlanPdfHtml('P700014',items).match(/class="uplm-export-section"/g)).toHaveLength(2)
})