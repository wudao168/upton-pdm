import {describe,it,expect} from 'vitest'
import {manufacturingValue,manufacturingStats} from '../src/procurementManufacturing'
import type {ProjectProcurementTrackingItem} from '../src/types'

function row():ProjectProcurementTrackingItem {
  return {sequence:1,projectCode:'P1',materialCode:'0204',materialName:'支架',quantity:10,bomKind:'非标件',purchaseRequisitionNumbers:[],purchaseRequisitionStatus:'',purchaseOrderNumbers:[],purchaseOrderStatus:'',purchaseQuantity:999,arrivedQuantity:999,requestedQuantity:null,approvedQuantity:null,details:[],impactStage:'Assembly',assemblyStartDate:'2026-10-25',manufacturing:{releasePackageId:'release',lineId:'line',revision:'A',publishedAt:'2026-10-20',receivedAt:'2026-10-21',status:'分批流转',assignedQuantity:10,submittedQuantity:8,qualifiedQuantity:5,issuedQuantity:2,requiredOn:'2026-10-24',forecastOn:'2026-10-26',batches:[{id:'batch',route:'External',quantity:10,status:'质检',promisedOn:'2026-10-26',deliveredQuantity:10,submittedQuantity:8,qualifiedQuantity:5,issuedQuantity:2,history:[]}]}}
}
describe('非标件制造跟踪口径',()=>{
  it('交检、合格、领用分别取制造记录，不采用采购数或到货数',()=>{
    const r=row();expect(manufacturingValue(r,'purchaseQuantity')).toBe(10);expect(manufacturingValue(r,'arrivedQuantity')).toBe(8)
    expect(manufacturingValue(r,'receiptQuantity')).toBe(5);expect(manufacturingValue(r,'issueQuantity')).toBe(2);expect(manufacturingValue(r,'inventoryQuantity')).toBe(3)
    expect(manufacturingValue(r,'purchaseRequisitionNumbers')).toEqual([])
    const stats=manufacturingStats([r],'2026-10-27');expect(stats).toMatchObject({total:1,complete:0,overdue:1,mismatch:1,shortage:1,unassigned:0})
    r.manufacturing!.submittedQuantity=10;expect(manufacturingStats([r],'2026-10-27').complete).toBe(0)
    r.manufacturing!.qualifiedQuantity=10;expect(manufacturingStats([r],'2026-10-27')).toMatchObject({complete:1,overdue:0,mismatch:0,shortage:0})
  })
  it('未完全分配且整行交期待确认时，仍检查已有未完成批次的过期承诺',()=>{
    const r=row();r.manufacturing!.assignedQuantity=6;r.manufacturing!.forecastOn=null;r.manufacturing!.batches[0]!.promisedOn='2026-10-20'
    expect(manufacturingStats([r],'2026-10-22')).toMatchObject({overdue:1,unassigned:1})
    r.manufacturing!.qualifiedQuantity=1;r.manufacturing!.issuedQuantity=2;expect(manufacturingValue(r,'inventoryQuantity')).toBe(0)
  })
  it('制造齐套统计排除标准件、仓库明细和零需求记录',()=>{
    const r=row(),standard={...r,bomKind:'标准件',manufacturing:undefined},cancelled={...r,quantity:0}
    expect(manufacturingStats([r,standard,cancelled],'2026-10-22').total).toBe(1)
    expect(manufacturingValue(standard,'arrivedQuantity')).toBe(999)
  })
})
