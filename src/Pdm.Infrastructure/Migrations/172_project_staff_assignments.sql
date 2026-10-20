-- 将原有工程师角色分配固化为项目自己的岗位记录；保留原执行工程师及既有阶段负责人。
INSERT IGNORE INTO project_assignment(project_id,username,assignment_type,assigned_by,assigned_at)
SELECT assignment.project_id,assignment.username,'PhaseOwner:ElectricalEngineer',assignment.assigned_by,assignment.assigned_at
FROM project_assignment assignment
INNER JOIN pdm_user account ON account.username=assignment.username
WHERE assignment.assignment_type='Designer'
  AND (account.assigned_role_code='ElectricalEngineer' OR account.role='ElectricalEngineer'
       OR EXISTS(SELECT 1 FROM pdm_user_role user_role WHERE user_role.user_id=account.id AND user_role.role_code='ElectricalEngineer'));

INSERT IGNORE INTO project_assignment(project_id,username,assignment_type,assigned_by,assigned_at)
SELECT assignment.project_id,assignment.username,'PhaseOwner:ElectricalHardware',assignment.assigned_by,assignment.assigned_at
FROM project_assignment assignment
INNER JOIN pdm_user account ON account.username=assignment.username
WHERE assignment.assignment_type='Designer'
  AND (account.assigned_role_code='HardwareEngineer' OR account.role='HardwareEngineer'
       OR EXISTS(SELECT 1 FROM pdm_user_role user_role WHERE user_role.user_id=account.id AND user_role.role_code='HardwareEngineer'));

-- 原先显示的共享负责人复制到尚未独立配置的子项目，后续修改或清空互不影响。
INSERT IGNORE INTO project_assignment(project_id,username,assignment_type,assigned_by,assigned_at)
SELECT child.id,assignment.username,assignment.assignment_type,assignment.assigned_by,assignment.assigned_at
FROM project child
INNER JOIN project_assignment assignment ON assignment.project_id=COALESCE(child.root_project_id,child.parent_project_id)
WHERE child.parent_project_id IS NOT NULL
  AND assignment.assignment_type IN ('PhaseOwner:ElectricalHardware','PhaseOwner:MechanicalAssembly','PhaseOwner:ElectricalAssembly')
  AND NOT EXISTS(SELECT 1 FROM project_assignment existing WHERE existing.project_id=child.id AND existing.assignment_type=assignment.assignment_type);
