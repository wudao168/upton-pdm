import type { BomConfigurationQuantityChange, BomItem, MaterialRelationAllocation, MaterialRelationCompleteness } from './types'

type Check = MaterialRelationCompleteness['mainMaterials'][number]
export type ConfigurationRow = BomItem & {
  _quantityMembers?: BomConfigurationQuantityChange['members']
  _configurationName?: string
  _configurationKey?: string
  _configurationChild?: boolean
  _configurationOutside?: boolean
  _configurationKitComponent?: boolean
  _configurationChanged?: boolean
  _previewNew?: boolean
  _configurationOtherChildren?: number
  _configurationChildrenSeparate?: boolean
  _kitReferenceIds?: string[]
}
export interface ConfigurationGroup { key: string; row: ConfigurationRow; kit: boolean; children: ConfigurationRow[]; consumed: Set<BomItem> }
function sourceIds(row:BomItem) {return row.summaryItemIds ?? (row as BomItem & {_sourceItemIds?:string[]})._sourceItemIds ?? (row.id?[row.id]:[])}

function allocationRecipe(allocation: MaterialRelationAllocation | undefined, check: Check | undefined, accessories: BomItem[], total: number) {
  const mode=(value:unknown)=>value===1||value==='Fixed'?'Fixed':'PerMainQuantity'
  if (allocation) return allocation.groups.map(group => {
    const options=group.options?.length ? group.options : check?.groups.find(item=>item.groupId===group.groupId)?.options.filter(option=>group.optionIds.includes(option.id)) ?? []
    return [group.groupId, group.optionIds.length?'Selected':group.confirmNoAccessory?'NoAccessory':'Pending', options.map(option=>[option.materialCode,option.unitCode,mode(option.quantityMode),option.quantityPerSet]).sort()]
  }).sort()
  if (check) return check.groups.map(group => [group.groupId, group.selectedOptionIds.length?'Selected':group.reviewDecision ?? 'Pending',
    group.options.filter(option => group.selectedOptionIds.includes(option.id)).map(option => [option.materialCode, option.unitCode, mode(option.quantityMode), option.quantityPerSet]).sort()]).sort()
  return accessories.map(row => [row.drawingNumber, row.unit, total ? row.quantity / total : 0]).sort()
}
export function buildBomConfigurationGroups(rows: BomItem[], checks: Check[], kitParent: (source: BomItem) => BomItem,
  published: (row: BomItem) => string): ConfigurationGroup[] {
  const result = new Map<string, ConfigurationGroup>()
  const ownerIds = new Set(rows.flatMap(row => row.relationMainBomItemId ? [row.relationMainBomItemId] : []))
  const kits = new Map<string, BomItem>()
  for (const row of rows) if (row.engineeringKitReferenceId && !kits.has(row.engineeringKitReferenceId)) kits.set(row.engineeringKitReferenceId, row)
  const owners: Array<{id: string; main: BomItem; kit: boolean; check?: Check}> = [...kits].map(([id, source]) => {
    const main=kitParent(source),actual=checks.find(check=>check.mainBomItemId===id),template=checks.find(check=>check.mainMaterialCode===source.engineeringKitCode)
    const check=actual ?? (template?{...template,mainBomItemId:id,mainQuantity:main.quantity,allocations:[],isComplete:false,
      groups:template.groups.map(group=>({...group,selectedOptionIds:[],reviewDecision:null,accessoryBomItemIds:{},actualQuantity:0,expectedQuantity:0,isComplete:false}))}:undefined)
    return {id,main,kit:true,check}
  })
  for (const check of checks) {
    if (kits.has(check.mainBomItemId)) continue
    const source = rows.find(row => sourceIds(row).includes(check.mainBomItemId))
    if (source) owners.push({id:check.mainBomItemId, main:{...source,id:check.mainBomItemId,quantity:sourceIds(source).length > 1 ? check.mainQuantity : source.quantity},kit:false,check})
  }
  for (const row of rows) if (row.id && !owners.some(owner => owner.id === row.id)
    && (row.relationAllocations?.length || ownerIds.has(row.id))) owners.push({id:row.id,main:row,kit:false})
  for (const owner of owners) {
    const own = rows.filter(row => owner.kit ? row.engineeringKitReferenceId === owner.id : sourceIds(row).includes(owner.id))
    const accessories = rows.filter(row => row.relationMainBomItemId === owner.id)
    const allocations = owner.main.relationAllocations?.length ? owner.main.relationAllocations : owner.check?.allocations ?? []
    const variants: Array<MaterialRelationAllocation | undefined> = allocations.length ? allocations : [undefined]
    const publication = [...new Set(own.map(published))].sort().join('、') || published(owner.main)
    for (const allocation of variants) {
      const quantity = allocation && allocations.length>1 ? allocation.quantity : owner.main.quantity
      const key = JSON.stringify([owner.main.kind, owner.kit ? owner.main.engineeringKitCode : owner.main.drawingNumber, owner.main.unit,
        owner.main.engineeringKitRevisionId, owner.main.engineeringKitVersionNumber, publication,owner.main.bomPublishedVersion,
        !!owner.main.pendingRemoval, !!owner.main.manuallyExcluded, allocation?.revisionId ?? owner.check?.revisionId, allocationRecipe(allocation, owner.check, accessories, owner.main.quantity),
        owner.kit ? own.map(row => [row.engineeringKitComponentId, row.relationAllocations?.map(value => allocationRecipe(value,undefined,[],row.quantity))]).sort() : null])
      const waiting = !allocation && owner.check?.groups.some(group => !group.selectedOptionIds.length && group.reviewDecision !== 'NoAccessory')
      const name = waiting ? '待选配' : allocation?.name.replace(/^组合\s*(\d+)$/, (_, value: string) => {
        let order=Number(value), letters='';while(order>0){order--;letters=String.fromCharCode(65+order%26)+letters;order=Math.floor(order/26)}return '组合'+letters
      }) || (accessories.length || owner.check?.groups.some(group => group.selectedOptionIds.length) ? '统一配置' : owner.check ? '无需配套' : '标准配置')
      let group = result.get(key)
      if (!group) {
        group = {key,kit:owner.kit,children:[],consumed:new Set(),row:{...owner.main,quantity:0,displayRevision:publication,
          _quantityMembers:[],_configurationName:name,_configurationKey:key,_kitReferenceIds:[],_configurationKitComponent:!owner.kit && !!owner.main.engineeringKitReferenceId}}
        result.set(key,group)
      }
      group.row.quantity += quantity
      group.row.sequence = Math.min(group.row.sequence, owner.main.sequence)
      group.row._quantityMembers!.push({mainBomItemId:owner.id,allocationId:allocation?.id})
      if(owner.kit && own.some(row=>owners.some(candidate=>!candidate.kit && candidate.id===row.id)))group.row._configurationChildrenSeparate=true
      if(owner.kit)group.row._kitReferenceIds!.push(owner.id)
      own.forEach(row=>group!.consumed.add(row));accessories.forEach(row=>group!.consumed.add(row))
      const addChild = (row: BomItem, amount: number) => {
        const existing = group!.children.find(item => item.kind === row.kind && item.drawingNumber === row.drawingNumber && item.unit === row.unit && item.displayRevision === published(row))
        if(existing)existing.quantity += amount
        else group!.children.push({...row,quantity:amount,displayRevision:published(row),_configurationChild:true})
      }
      if(owner.kit)for(const row of own.filter(row=>!owners.some(candidate=>!candidate.kit && candidate.id===row.id)))
        addChild(row, owner.main.quantity ? row.quantity / owner.main.quantity * quantity : row.quantity)
      if(allocation) {
        const options = allocation.groups.flatMap(group => group.options ?? [])
        for(const option of options) {
          const mapped=owner.check?.groups.map(group=>group.accessoryBomItemIds?.[option.optionId]).find(Boolean)
          const source = accessories.find(row=>row.relationOptionId===option.optionId)
            ?? accessories.find(row=>mapped && sourceIds(row).includes(mapped))
            ?? accessories.find(row=>row.drawingNumber === option.materialCode)
          if(source)addChild(source, (option.quantityMode === 'Fixed' || option.quantityMode === 1) ? option.quantityPerSet : quantity * option.quantityPerSet)
        }
        if(!options.length)for(const row of accessories)addChild(row,row.quantity / owner.main.quantity * quantity)
      } else for(const row of accessories)addChild(row,row.quantity)
    }
  }
  return [...result.values()]
}
