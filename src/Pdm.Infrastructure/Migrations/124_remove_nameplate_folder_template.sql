-- 默认目录模板不再包含"铭牌"目录；新项目不再自动生成，旧模板已生成的空"铭牌"目录一并清理。
DELETE FROM folder_template_node WHERE folder_key='mechanical.nameplate';

-- 只删除空的"铭牌"目录：有子目录、图档或项目文件的目录保留，避免误删资料。
DELETE folder
    FROM project_folder folder
    LEFT JOIN project_folder child ON child.parent_folder_id=folder.id
    LEFT JOIN document document ON document.folder_id=folder.id
    LEFT JOIN project_file file ON file.folder_id=folder.id
    WHERE folder.template_key='mechanical.nameplate'
      AND child.id IS NULL AND document.id IS NULL AND file.id IS NULL;
