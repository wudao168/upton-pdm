import { budgetCategories } from './projectBudget'

export type ApplicationBudgetAmounts = Record<string, number | null>
export const copyApplicationBudget = (amounts?: ApplicationBudgetAmounts | null): ApplicationBudgetAmounts =>
  Object.fromEntries(budgetCategories.map(category => [category.key, amounts?.[category.key] ?? null]))
export const completeApplicationBudget = (amounts?: ApplicationBudgetAmounts | null) => budgetCategories.every(category =>
  typeof amounts?.[category.key] === 'number' && Number.isFinite(amounts[category.key]) && amounts[category.key]! >= 0 && amounts[category.key]! <= 1_000_000_000)
export const applicationBudgetTotal = (amounts: ApplicationBudgetAmounts, labor?: boolean) => {
  const values = budgetCategories.filter(category => labor === undefined || category.labor === labor)
    .map(category => amounts[category.key]).filter((value): value is number => value != null && Number.isFinite(value))
  return values.length ? values.reduce((sum, value) => sum + Math.round(value * 100), 0) / 100 : null
}
