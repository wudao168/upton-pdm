export interface ApprovalNodePolicy {
  requireOpinion?: boolean
  editBusinessNote?: boolean
  disableTransfer?: boolean
}
export interface ApprovalNoteRevision { actor: string; text: string; at: string }
export interface ApprovalParticipants {
  departments?: { unitId: string; name: string; members: string[]; requireAll: boolean; complete: boolean; signatures: { actor: string; comment?: string | null; signedAt: string }[]; rejectedBy?: string | null; rejection?: string | null }[]
  policy?: ApprovalNodePolicy | null
  noteHistory?: ApprovalNoteRevision[]
  members: string[]
  requireAll: boolean
  signatures: { actor: string; comment?: string | null; signedAt: string }[]
}

export function canSignNode(node: { assignee: string; participants?: ApprovalParticipants | null }, username: string) {
  const matches = (name: string) => name.toLowerCase() === username.toLowerCase()
  const group = node.participants
  if (group?.departments?.length) return group.departments.some(d => !d.complete && !d.rejectedBy && d.members.some(matches) && !d.signatures.some(s => matches(s.actor)))
  return group ? group.members.some(matches) && !group.signatures.some(signature => matches(signature.actor)) : matches(node.assignee)
}
