import type { DocumentNode } from './types'

function leafName(path: string) {
  return (path.replaceAll('\\', '/').split('/').pop() ?? '').trim().toLowerCase()
}

function modelName(path: string) {
  return leafName(path).replace(/\.(sldasm|sldprt|easm|eprt)$/i, '')
}

export function findPreviewComponent(root: DocumentNode, name: string, configuration: string) {
  const path = (name.split('@')[0] ?? '').replaceAll('\\', '/').toLowerCase()
    .split('/').map(segment => segment.split('~')[0]?.trim() ?? '').join('/')
  const occurrence = leafName(path)
  if (!occurrence) return undefined
  const componentName = modelName(occurrence)
  const fileStem = componentName.replace(/-\d+$/, '')
  const config = configuration.trim().toLowerCase()
  const pathMatches: DocumentNode[] = []
  const occurrenceMatches: DocumentNode[] = []
  const fileMatches: DocumentNode[] = []
  const visit = (node: DocumentNode, parentPath: string) => {
    const instance = leafName(node.instancePath ?? '')
    const nodePath = instance ? parentPath + '/' + instance : parentPath
    if (node.kind !== 'Drawing' && (!config || node.configuration.trim().toLowerCase() === config)) {
      if (instance === occurrence) {
        occurrenceMatches.push(node)
        if (parentPath && path.endsWith(nodePath)) pathMatches.push(node)
      }
      const candidate = modelName(node.fileName)
      if (candidate === componentName || candidate === fileStem) fileMatches.push(node)
    }
    node.children.forEach(child => visit(child, nodePath))
  }
  root.children.forEach(child => visit(child, ''))

  // 同一图档的多个实例可共享属性。不同图档或配置同名时保留未匹配状态。
  const matches = pathMatches.length ? pathMatches : occurrenceMatches.length ? occurrenceMatches : fileMatches
  const first = matches[0]
  return first?.documentId && matches.every(node =>
    node.documentId === first.documentId && node.configuration === first.configuration) ? first : undefined
}
