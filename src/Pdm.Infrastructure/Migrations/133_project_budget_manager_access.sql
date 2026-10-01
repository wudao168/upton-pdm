UPDATE pdm_system_setting SET setting_value=JSON_ARRAY_APPEND(setting_value,'$.rules.MainManager','project.budget.view')
WHERE setting_key='project_permission_settings' AND JSON_CONTAINS_PATH(setting_value,'one','$.rules.MainManager')
AND NOT JSON_CONTAINS(JSON_EXTRACT(setting_value,'$.rules.MainManager'),JSON_QUOTE('project.budget.view'));
UPDATE pdm_system_setting SET setting_value=JSON_ARRAY_APPEND(setting_value,'$.rules.ChildManager','project.budget.view')
WHERE setting_key='project_permission_settings' AND JSON_CONTAINS_PATH(setting_value,'one','$.rules.ChildManager')
AND NOT JSON_CONTAINS(JSON_EXTRACT(setting_value,'$.rules.ChildManager'),JSON_QUOTE('project.budget.view'));
