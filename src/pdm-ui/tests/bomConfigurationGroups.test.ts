import {describe,expect,it} from 'vitest'
import {buildBomConfigurationGroups} from '../src/bomConfigurationGroups'
import type {BomItem,MaterialRelationAllocation,MaterialRelationCompleteness} from '../src/types'

type Check=MaterialRelationCompleteness['mainMaterials'][number]
const main=(id:string,quantity=1):BomItem=>({id,kind:'Standard',sequence:1,drawingNumber:'MOTOR',name:'主料',quantity,unit:'001',revision:'W1',complete:true})
const option=(code:string,mode:'Fixed'|'PerMainQuantity'='PerMainQuantity')=>({optionId:code,materialCode:code,materialName:code,unitCode:'001',quantityMode:mode,quantityPerSet:1,quantity:1})
const allocation=(id:string,name:string,code:string,quantity=1):MaterialRelationAllocation=>({id,name,quantity,revisionId:'relation-v1',revisionVersion:1,
  groups:[{groupId:'group',groupName:'配件',optionIds:[code],confirmNoAccessory:false,options:[option(code)]}]})
const parent=(row:BomItem)=>({...row,drawingNumber:row.engineeringKitCode!,name:'套件',quantity:row.quantity/2,relationAllocations:row.engineeringKitRelationAllocations})
const published=(row:BomItem)=>row.displayRevision||'未发布'

describe('每个折叠栏只显示一种配置',()=>{
  it('同版未选配套件合并，已发布和不同套件版本分开',()=>{
    const kit={...main('part'),drawingNumber:'PART',quantity:2,engineeringKitCode:'UKIT-1',engineeringKitReferenceId:'ref-a',engineeringKitRevisionId:'kit-v1',engineeringKitVersionNumber:1,engineeringKitComponentId:'component'}
    const groups=buildBomConfigurationGroups([kit,{...kit,id:'b',engineeringKitReferenceId:'ref-b'},{...kit,id:'released',engineeringKitReferenceId:'ref-pub',displayRevision:'S-V07'},
      {...kit,id:'v2',engineeringKitReferenceId:'ref-v2',engineeringKitRevisionId:'kit-v2',engineeringKitVersionNumber:2}],[],parent,published)
    expect(groups).toHaveLength(3);expect(groups.map(group=>group.row.quantity)).toEqual([2,1,1])
    expect(groups[0].children).toHaveLength(1);expect(groups[0].children[0].quantity).toBe(4)
    expect(groups[0].row._quantityMembers).toHaveLength(2)
  })
  it('一个主料有两种组合时分为两个折叠栏，各自只含自己的配件',()=>{
    const source={...main('main',2),relationAllocations:[allocation('a','组合A','ACC-A'),allocation('b','组合B','ACC-B')]}
    const accessories=['ACC-A','ACC-B'].map(code=>({...main(code),drawingNumber:code,relationMainBomItemId:source.id,source:'MaterialRelation' as const}))
    for(const state of ['未发布','S-V07']){
      const groups=buildBomConfigurationGroups([{...source,displayRevision:state},...accessories],[],parent,published)
      expect(groups).toHaveLength(2);expect(groups.map(group=>group.row._configurationName)).toEqual(['组合A','组合B'])
      expect(groups.map(group=>group.children.map(row=>row.drawingNumber))).toEqual([['ACC-A'],['ACC-B']])
      expect(groups.map(group=>group.row.quantity)).toEqual([1,1])
    }
  })
  it('组合名称与内部编号不同但实际配件用量相同可以汇总',()=>{
    const a={...main('main-a',2),relationAllocations:[allocation('allocation-a','组合A','ACC',2)]}
    const b={...main('main-b',3),relationAllocations:[allocation('allocation-b','自定义名称','ACC',3)]}
    const accessories=[a,b].map(owner=>({...main('acc-'+owner.id,owner.quantity),drawingNumber:'ACC',relationMainBomItemId:owner.id}))
    const groups=buildBomConfigurationGroups([a,b,...accessories],[],parent,published)
    expect(groups).toHaveLength(1);expect(groups[0].row.quantity).toBe(5);expect(groups[0].children[0].quantity).toBe(5)
  })
  it('待选配可以汇总，已确认无需配套单独显示',()=>{
    const sources=['a','b','none'].map(id=>main(id))
    const checks=sources.map(row=>({mainBomItemId:row.id!,mainMaterialCode:row.drawingNumber,mainMaterialName:row.name,mainQuantity:1,templateId:'template',revisionId:'revision',revisionVersion:1,isComplete:row.id==='none',
      groups:[{groupId:'group',groupName:'配件',isRequired:false,selectionMode:'Single',maxSelection:1,isComplete:row.id==='none',status:'待核对',expectedQuantity:0,actualQuantity:0,selectedOptionIds:[],reviewDecision:row.id==='none'?'NoAccessory':null,options:[]}]} as Check))
    const groups=buildBomConfigurationGroups(sources,checks,parent,published)
    expect(groups).toHaveLength(2);expect(groups[0].row._configurationName).toBe('待选配');expect(groups[0].row.quantity).toBe(2)
    expect(groups[1].row._configurationName).toBe('无需配套')
  })
})
