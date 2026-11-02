import type { MaterialRelationMainMaterial, MaterialRelationTemplate } from './types'

export function relationMains(relation: MaterialRelationTemplate, draft = true): MaterialRelationMainMaterial[] {
  const revision = (draft ? relation.draftRevision : null) ?? relation.publishedRevision
  return revision?.mainMaterials?.length ? revision.mainMaterials
    : (draft ? relation.mainMaterials : relation.publishedMainMaterials)?.length ? (draft ? relation.mainMaterials : relation.publishedMainMaterials)!
      : !draft && !relation.publishedRevision ? [] : [{ materialId: relation.mainMaterialId, materialCode: relation.mainMaterialCode, materialName: relation.mainMaterialName, standardLibraryCategories: relation.standardLibraryCategories }]
}

export function relationHasMain(relation: MaterialRelationTemplate, materialId: string) {
  return [...relationMains(relation), ...relationMains(relation, false)].some(main => main.materialId === materialId)
}
