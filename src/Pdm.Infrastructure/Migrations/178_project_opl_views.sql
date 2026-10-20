-- 历史记录没有可靠的查看历史，以本迁移 applied_at 作为 NEW 提醒上线基线。

CREATE TABLE project_opl_issue_view (
    issue_id BINARY(16) NOT NULL,
    username VARCHAR(191) NOT NULL,
    viewed_at DATETIME(6) NOT NULL,
    PRIMARY KEY (issue_id, username),
    CONSTRAINT fk_project_opl_view_issue FOREIGN KEY (issue_id) REFERENCES project_opl_issue(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
