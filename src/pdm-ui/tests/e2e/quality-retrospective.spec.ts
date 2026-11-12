import { test, expect } from '@playwright/test'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
// Browser plugin not available. Playwright exercises built UI on LAN HTTP with isolated API responses.
// Flow: project center -> summary draft -> submit/review/publish -> frozen issue snapshot -> experience adoption.
test('project retrospective preserves published snapshot and confirms assigned experience over LAN HTTP',async({page})=>{
  test.setTimeout(120000)
  const errors:string[]=[],writes:string[]=[]
  page.on('pageerror',reason=>errors.push(reason.message));page.on('console',entry=>{if(entry.type()==='error')errors.push(entry.text())})
  const projectId='71111111-1111-1111-1111-111111111111',companyId='72222222-2222-2222-2222-222222222222',issueId='73333333-3333-3333-3333-333333333333'
  const permissions=['project.view','system.project-center-permissions.v1','project.summary.view','project.summary.manage','quality.summary.review','project.summary.publish','project.summary.adoption.confirm','project.quality.view','project.content.view','project.content.write']
  const session={accessToken:'summary-browser-fixture',expiresAt:'2099-01-01T00:00:00Z',username:'summary-qa',displayName:'总结验收员',role:'Engineer',permissions,primaryCompanyId:companyId,activeCompanyId:companyId,activeCompanyName:'总结验收公司',accessibleCompanies:[{id:companyId,name:'总结验收公司',code:'QA'}]}
  const project={id:projectId,rootProjectId:projectId,code:'QA-SUM-001',name:'项目总结验证设备',stage:'设计中',owner:'summary-qa',primaryProjectManager:'summary-qa',collaborativeProjectManagers:[],designers:[],quantity:1,serialNumbers:[],responsibleUsers:[],canReadContent:true,isActive:true,organizationId:companyId,customerName:'验收客户',documentCount:0,vaultName:'测试图档库',vaultLocation:'',releaseLocation:'',permissionSchemaVersion:2,effectiveProjectPermissions:permissions}
  const issue={id:issueId,projectId,number:1,description:'夹具定位重复精度不足',priority:'普通',status:'待处理',owner:'engineer',dueDate:'2026-12-01',solution:'补充定位检查',createdBy:'summary-qa',createdAt:'2026-11-01T00:00:00Z',completedAt:null,updatedBy:'summary-qa',updatedAt:'2026-11-01T00:00:00Z',rowVersion:1,attachments:[]}
  const payload={projectId,rowVersion:1,summaries:[] as any[],issues:[{issue,profile:null,rootProjectId:projectId,projectCode:project.code,projectName:project.name,ownerName:'张工',createdByName:'总结验收员',canOpenProject:true}],
    experiences:[{id:'experience-1',improvementId:'improvement-1',title:'定位基准统一检查',content:'装配前核对统一定位基准并记录检测结果。',applicability:'定位工装类设备',publishedBy:'summary-qa',publishedAt:'2026-11-01T00:00:00Z',revision:1}],
    adoptions:[{id:'adoption-1',experienceId:'experience-1',projectId,owner:'engineer',dueOn:'2026-12-01',state:'待检查',evidence:''}],people:[{username:'engineer',displayName:'张工'}],themes:[],canEdit:true,canReview:true,canPublish:true,canAdopt:true,canViewIssues:true}
  await page.route('**/api/**',async route=>{
    const path=new URL(route.request().url()).pathname,method=route.request().method(),reply=(json:unknown)=>route.fulfill({json})
    if(path==='/api/auth/login'||path==='/api/auth/resume')return reply(session)
    if(path==='/api/auth/me'||path==='/api/auth/profile')return reply({username:session.username,displayName:session.displayName})
    if(path==='/api/me/table-preferences')return reply({preferences:{}})
    if(path==='/api/organization-directory')return reply({organizations:[{id:companyId,code:'QA',name:'总结验收公司',isActive:true}],units:[],departments:[],users:[]})
    if(path==='/api/system-connections')return reply({pdf:{state:'unconfigured',message:'未配置'},u9c:{state:'unconfigured',message:'未配置'}})
    if(path==='/api/materials/notification-permission')return reply({enabled:false})
    if(path==='/api/workbench')return reply({projects:[],notes:[],risks:[],scope:'company',companyName:'总结验收公司'})
    if(path==='/api/projects')return reply([project])
    if(path===`/api/projects/${projectId}`)return reply(project)
    if(path===`/api/projects/${projectId}/quality-review`)return reply(payload)
    if(path.includes('/retrospectives/')&&method==='PUT'){
      const input=route.request().postDataJSON(),id=path.split('/').at(-1)!;expect(input.coverageFrom).toBe('2026-11-01');expect(input.coverageTo).toBe('2026-11-10');expect(input.targetResults).toContain('预算');
      payload.summaries=[{...input,id,projectId,revision:1,state:'草稿',createdBy:'summary-qa',createdAt:'2026-11-11T00:00:00Z',reviewedBy:null,publishedAt:null,issues:[]}];payload.rowVersion++;writes.push('save');return reply({rowVersion:payload.rowVersion})
    }
    if(path.includes('/retrospectives/')&&path.endsWith('/transition')){
      const input=route.request().postDataJSON(),item=payload.summaries[0];expect(input.note).toBeTruthy()
      if(input.action==='submit'){item.state='待复核';item.issues=[{issueId,number:1,projectId,projectCode:project.code,description:issue.description,status:issue.status,ownerName:'张工',solution:issue.solution,profile:null,sourceRowVersion:1}]}
      if(input.action==='review')item.state='已复核'
      if(input.action==='publish'){item.state='已发布';item.publishedAt='2026-11-11T01:00:00Z'}
      payload.rowVersion++;writes.push(input.action);return reply({rowVersion:payload.rowVersion})
    }
    if(path==='/api/quality-review/adoptions/adoption-1/confirm'){
      const input=route.request().postDataJSON();expect(input.state).toBe('已采用');expect(input.evidence).toContain('检查表');payload.adoptions[0]!.state=input.state;payload.adoptions[0]!.evidence=input.evidence;payload.rowVersion++;writes.push('adopt');return reply({rowVersion:payload.rowVersion})
    }
    return reply([])
  })
  await page.route('**/health',route=>route.fulfill({json:{status:'ok'}}))
  await page.route('**/*jelly*.html*',route=>new URL(route.request().url()).searchParams.has('import')?route.fallback():route.fulfill({contentType:'text/html',body:'<html></html>'}))
  await page.setViewportSize({width:1440,height:1000});await page.goto('/');await expect(page).toHaveTitle(/UPLM|UPTON|PLM/)
  const login=page.getByLabel('登录PLM');await login.getByRole('textbox',{name:'账号'}).fill('summary-qa');await login.getByRole('textbox',{name:'密码'}).fill('fixture-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  await page.locator('.pdm-sidebar').getByRole('button',{name:'项目中心',exact:true}).click()
  const tabs=page.getByRole('navigation',{name:'项目功能'});await expect(tabs).toBeVisible();await tabs.getByRole('button',{name:'总结',exact:true}).click()
  const panel=page.getByRole('region',{name:'项目总结',exact:true});await expect(panel.getByText('暂无项目总结。')).toBeVisible()
  await panel.getByRole('button',{name:'新增总结',exact:true}).click();await panel.getByLabel('总结标题').fill('设计阶段复盘');await panel.getByLabel('总结开始日期').fill('2026-11-01');await panel.getByLabel('总结结束日期').fill('2026-11-10');await panel.getByLabel('进度、预算与质量目标达成').fill('设计按期完成，预算无超支，定位问题跟踪中。');await panel.getByLabel('成果与做得好的方面').fill('完成主要工站设计');await panel.getByLabel('主要问题与经验教训').fill('定位基准应在设计评审时统一');await panel.getByLabel('遗留事项、责任安排及后续计划').fill('张工负责补充定位检查表，12月1日前完成。');await panel.getByRole('button',{name:'保存草稿',exact:true}).click();await expect(panel.getByText('总结草稿已保存。')).toBeVisible()
  for(const [label,expected] of [['提交复核','待复核'],['复核通过','已复核'],['发布总结','已发布']]){
    await panel.getByRole('button',{name:label,exact:true}).click();const dialog=page.getByRole('dialog',{name:label,exact:true});await dialog.getByRole('textbox').fill('验收确认，资料完整。');await dialog.getByRole('button',{name:'确认',exact:true}).click();await expect(panel.locator('table').first().getByRole('cell',{name:expected,exact:true})).toBeVisible()
  }
  issue.status='已完成';await panel.getByRole('button',{name:'刷新',exact:true}).click();const snapshot=panel.getByRole('table').last();await expect(snapshot.getByRole('cell',{name:'待处理',exact:true})).toBeVisible();await expect(snapshot.getByRole('cell',{name:'已完成',exact:true})).toBeVisible();await expect(panel.getByRole('button',{name:'编辑',exact:true})).toHaveCount(0)
  await page.screenshot({path:join(tmpdir(),'pdm-quality-retrospective-published.png'),fullPage:true})
  await panel.getByRole('tab',{name:'经验采用',exact:true}).click();await panel.getByLabel('实施证据或不适用原因').fill('已在项目检查表增加定位基准核对，并完成首台验证。');await panel.getByRole('button',{name:'确认落实',exact:true}).click();await expect(panel.getByText('已采用',{exact:true})).toBeVisible();await expect(panel.getByRole('button',{name:'确认落实',exact:true})).toHaveCount(0)
  await page.setViewportSize({width:760,height:900});await expect(panel.getByText('定位基准统一检查',{exact:true})).toBeVisible();expect(await panel.evaluate(el=>el.scrollWidth<=el.clientWidth+2)).toBe(true);await page.screenshot({path:join(tmpdir(),'pdm-quality-retrospective-adoption-narrow.png'),fullPage:true})
  await expect(page.locator('vite-error-overlay')).toHaveCount(0);expect(writes).toEqual(['save','submit','review','publish','adopt']);expect(errors).toEqual([])
})
