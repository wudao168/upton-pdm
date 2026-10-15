export interface DingTalkGroup { openConversationId?: string; id: string; name: string; registered?: boolean; projectCount: number; boundProjects?: Array<{ id: string; code: string; name: string }>; entryUrl: string }

export interface DingTalkSettings { robotCode?: string; enabled: boolean; corpId: string; appKey: string; publicUrl: string; companyId: string; rowVersion: number; hasAppSecret: boolean; entryUrl: string; stream?: { state: string; message: string }; groups: DingTalkGroup[] }
export interface DingTalkBinding { rootProjectId: string; canManage: boolean; rowVersion: number; groupId: string | null; groups: DingTalkGroup[]; entryUrl: string; enabled: boolean; entryMessage?: string }
