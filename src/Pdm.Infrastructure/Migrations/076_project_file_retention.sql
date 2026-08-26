DROP TRIGGER IF EXISTS trg_project_file_version_no_delete;
CREATE TRIGGER trg_project_file_version_no_delete
BEFORE DELETE ON project_file_version
FOR EACH ROW
BEGIN
    IF COALESCE(@pdm_allow_project_file_retention_purge,0) <> 1 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='project file versions are immutable';
    END IF;
END;
