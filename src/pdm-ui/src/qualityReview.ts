import { PdmApiError, requestJson } from './api'
import type { OplIssue } from './projectOpl'

export interface QualityIssueProfile { issueId:string; domain:string; category:string; severity:string; occurredOn:string|null; themeId:string|null; duplicateOfIssueId:string|null; recurrenceOfIssueId:string|null; immediateAction:string; rootCause:string; correctiveAction:string; verificationResult:string; evidenceNote:string; evidenceAttachmentIds:string[]; updatedBy:string; updatedAt:string; verifiedBy?:string|null; verifiedAt?:string|null }
export interface QualityTheme { id:string; name:string; description:string; isActive:boolean }
export interface QualityIssueRow { issue:OplIssue; profile:QualityIssueProfile|null; rootProjectId:string; projectCode:string; projectName:string; ownerName:string; createdByName:string; canOpenProject:boolean }
export interface QualityThemeMetric { themeId:string; name:string; eventCount:number; projectCount:number; majorCount:number; isCandidate:boolean; activeImprovementId:string|null; unknownDateCount:number }
export interface QualityPerson { username:string; displayName:string }
export interface QualityProject { id:string; code:string; name:string; rootProjectId:string }
export interface QualityCapabilities { canManageIssues?:boolean; canManageThemes?:boolean; canManageImprovements?:boolean; canApproveImprovements?:boolean; canExecuteImprovements?:boolean; canVerifyImprovements?:boolean; canCloseImprovements?:boolean; canPublishExperience?:boolean; canReviewSummaries?:boolean; canManageSettings?:boolean; canViewDetails?:boolean; canVerifyIssues?:boolean; canCloseIssues?:boolean; canReopenIssues?:boolean; canVoidIssues?:boolean }
export interface QualityIssueSnapshot { issueId:string; number:number; projectId:string; projectCode:string; description:string; status:string; ownerName:string; solution:string; profile:QualityIssueProfile|null; sourceRowVersion:number }
export interface ProjectSummaryReview { id:string; projectId:string; revision:number; kind:string; state:string; title:string; achievements:string; lessons:string; remainingPlan:string; coverageFrom?:string|null; coverageTo?:string|null; targetResults?:string; createdBy:string; createdAt:string; reviewedBy:string|null; publishedAt:string|null; issues:QualityIssueSnapshot[] }
export interface QualityAction { id:string; title:string; owner:string; dueOn:string; completed:boolean; evidence:string; completedBy?:string|null; completedAt?:string|null }
export interface QualityObservation { projectId:string; appliedOn:string; observedOn:string; passed:boolean; evidence:string; recordedBy:string; recordedAt:string }
export interface QualityImprovementEvent { action:string; actor:string; actorName:string; at:string; note:string }
export interface QualityImprovementCycle { endedAt:string;reason:string;state:string;actions:QualityAction[];observations:QualityObservation[];observationStartedAt:string|null;verifiedBy:string|null;closedAt:string|null;minimumSamples:number;observationDays:number;maximumFailures:number }
export interface QualityImprovement { id:string; themeId:string; title:string; state:string; owner:string; rootCause:string; goal:string; dueOn:string; minimumSamples:number; observationDays:number; maximumFailures:number; actions:QualityAction[]; observations:QualityObservation[]; observationStartedAt:string|null; createdBy:string; createdAt:string; history:QualityImprovementEvent[]; verifiedBy?:string|null; closedAt?:string|null; previousCycles?:QualityImprovementCycle[] }
export interface QualityExperience { id:string; improvementId:string; title:string; content:string; applicability:string; publishedBy:string; publishedAt:string; revision:number; projectTags?:string[]; recommendedProjectIds?:string[] }
export interface QualityAdoption { id:string; experienceId:string; projectId:string; owner:string; dueOn:string; state:string; evidence:string; confirmedBy?:string|null; confirmedAt?:string|null }
export interface QualityCatalog { companyId:string; rowVersion:number; profiles:QualityIssueProfile[]; themes:QualityTheme[]; summaries:ProjectSummaryReview[]; improvements:QualityImprovement[]; experiences:QualityExperience[]; adoptions:QualityAdoption[]; settings:{windowDays:number;eventThreshold:number;projectThreshold:number} }
export interface QualityWorkspace { catalog:QualityCatalog; issues:QualityIssueRow[]; metrics:QualityThemeMetric[]; projects:QualityProject[]; people:QualityPerson[]; capabilities:QualityCapabilities; windowFrom:string; windowTo:string }
export interface RetrospectiveWorkspace {
  projectId:string; rowVersion:number; summaries:ProjectSummaryReview[]; issues:QualityIssueRow[]
  experiences:QualityExperience[]; adoptions:QualityAdoption[]; canEdit:boolean; canReview:boolean; canAdopt:boolean
  canPublish?:boolean; canViewIssues?:boolean; people:QualityPerson[]; themes:QualityTheme[]; improvements?:QualityImprovement[]
  summarySources?: Record<string, { projectCode:string; canEdit:boolean; canReview:boolean; canPublish:boolean }>
}
export interface QualityIssueWorkspace { rowVersion:number; row:QualityIssueRow; themes:QualityTheme[]; people:QualityPerson[]; relatedIssues:QualityIssueRow[]; canManageIssues:boolean; canVerifyIssues:boolean; canCloseIssues:boolean; canReopenIssues:boolean; canVoidIssues:boolean }
export interface QualitySettings { rowVersion:number; settings:{windowDays:number;eventThreshold:number;projectThreshold:number} }
export type SaveIssueProfile = Omit<QualityIssueProfile,'issueId'|'updatedBy'|'updatedAt'|'verifiedBy'|'verifiedAt'> & { expectedRowVersion:number }
export type SaveImprovement = Pick<QualityImprovement,'themeId'|'title'|'owner'|'rootCause'|'goal'|'dueOn'|'minimumSamples'|'observationDays'|'maximumFailures'|'actions'> & { expectedRowVersion:number }
export type SaveSummary = Pick<ProjectSummaryReview,'kind'|'title'|'achievements'|'lessons'|'remainingPlan'|'coverageFrom'|'coverageTo'|'targetResults'> & { expectedRowVersion:number }
type Saved = { rowVersion:number }
const part = encodeURIComponent
const write = (url:string, body:unknown, token:string, method='PUT') => requestJson<Saved>(url,{method,body:JSON.stringify(body)},token)
export const readQualityReview = (token:string) => requestJson<QualityWorkspace>('/api/quality-review',{},token)
export const readProjectRetrospective = (projectId:string,token:string) => requestJson<RetrospectiveWorkspace>(`/api/projects/${part(projectId)}/quality-review`,{},token)
export const readQualityIssue = (projectId:string,issueId:string,token:string) => requestJson<QualityIssueWorkspace>(`/api/projects/${part(projectId)}/quality-review/issues/${part(issueId)}`,{},token)
export const saveQualityIssue = (projectId:string,issueId:string,input:SaveIssueProfile,token:string) => write(`/api/projects/${part(projectId)}/quality-review/issues/${part(issueId)}`,input,token)
export const saveQualityTheme = (id:string,input:{expectedRowVersion:number;name:string;description:string;isActive:boolean},token:string) => write(`/api/quality-review/themes/${part(id)}`,input,token)
export const deleteQualityTheme = (id:string,expectedRowVersion:number,token:string) => requestJson<Saved>(`/api/quality-review/themes/${part(id)}?expectedRowVersion=${part(String(expectedRowVersion))}`,{method:'DELETE'},token)
export const saveRetrospective = (projectId:string,id:string,input:SaveSummary,token:string) => write(`/api/projects/${part(projectId)}/retrospectives/${part(id)}`,input,token)
export const transitionRetrospective = (projectId:string,id:string,expectedRowVersion:number,action:string,note:string,token:string) => write(`/api/projects/${part(projectId)}/retrospectives/${part(id)}/transition`,{expectedRowVersion,action,note},token,'POST')
export const saveQualityImprovement = (id:string,input:SaveImprovement,token:string) => write(`/api/quality-review/improvements/${part(id)}`,input,token)
export const transitionImprovement = (id:string,expectedRowVersion:number,action:string,note:string,token:string) => write(`/api/quality-review/improvements/${part(id)}/transition`,{expectedRowVersion,action,note},token,'POST')
export const saveQualityObservation = (id:string,input:{expectedRowVersion:number;projectId:string;appliedOn:string;observedOn:string;passed:boolean;evidence:string},token:string) => write(`/api/quality-review/improvements/${part(id)}/observations`,input,token,'POST')
export const publishQualityExperience = (id:string,input:{expectedRowVersion:number;improvementId:string;title:string;content:string;applicability:string;projectTags?:string[]},token:string) => write(`/api/quality-review/experiences/${part(id)}`,input,token)
export const assignQualityAdoption = (input:{expectedRowVersion:number;experienceId:string;projectIds:string[];owner:string;dueOn:string},token:string) => write('/api/quality-review/adoptions',input,token,'POST')
export const confirmQualityAdoption = (id:string,input:{expectedRowVersion:number;state:string;evidence:string},token:string) => write(`/api/quality-review/adoptions/${part(id)}/confirm`,input,token,'POST')
export const readQualitySettings = (token:string) => requestJson<QualitySettings>('/api/quality-review/settings',{},token)
export const saveQualitySettings = (input:QualitySettings['settings'] & {expectedRowVersion:number},token:string) => write('/api/quality-review/settings',input,token)

export function qualityFailure(error:unknown, fallback='操作未完成，请刷新数据、核对权限后重试。') {
  console.error('[质量与改善]',error)
  return error instanceof PdmApiError && error.status < 500 && /[\u3400-\u9fff]/.test(error.message) ? error.message : fallback
}
export const qualityName = (people:QualityPerson[]|undefined,username:string|null|undefined) => people?.find(person=>person.username===username)?.displayName?.trim() || '姓名待补充'
export const qualityDate = (value:string|null|undefined) => value ? value.slice(0,10) : '—'
export const qualityClosed = (state:string) => ['已完成','已关闭','已作废','已取消','已撤销'].includes(state)
export const qualityEventLabel = (action:string) => ({approve:'批准实施',observe:'开始观察','submit-verification':'提交验证',verify:'独立验证',close:'关闭改善',reopen:'重开修订','return-to-implementation':'退回实施',cancel:'撤销改善'}[action] ?? (/^[\u3400-\u9fff]/.test(action)?action:'操作记录'))
