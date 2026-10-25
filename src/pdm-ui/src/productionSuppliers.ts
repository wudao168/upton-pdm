export interface ProductionSupplierCategory { id: string; name: string; isActive: boolean }
export interface ProductionSupplier {
  id: string
  code: string
  name: string
  fullName: string
  categoryIds: string[]
  isActive: boolean
  address: string
  phone: string
  contact: string
  contactPhone: string
  remarks: string
  updatedAt: string
}
export interface ProductionSupplierCatalog {
  companyId: string
  categories: ProductionSupplierCategory[]
  suppliers: ProductionSupplier[]
  rowVersion: number
}
export type ProductionSupplierChoice = Pick<ProductionSupplier, 'id' | 'code' | 'name' | 'categoryIds' | 'isActive'>
export interface ProductionSupplierChoices { companyId: string; categories: ProductionSupplierCategory[]; suppliers: ProductionSupplierChoice[] }
export type SaveProductionSupplier = Omit<ProductionSupplier, 'id' | 'updatedAt'> & { expectedRowVersion: number }
