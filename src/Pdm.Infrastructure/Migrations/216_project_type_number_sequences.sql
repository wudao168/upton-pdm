-- 主项目按公司与类型独立计数。旧共享号池无法确定原类型，保留存档但不再用于自动取号。
CREATE TABLE IF NOT EXISTS project_type_number_counter (
    organization_id BINARY(16) NOT NULL,
    project_type_code VARCHAR(16) NOT NULL,
    current_value INT NOT NULL DEFAULT 0,
    PRIMARY KEY (organization_id, project_type_code),
    CONSTRAINT fk_project_type_counter_org FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS released_project_type_number (
    organization_id BINARY(16) NOT NULL,
    project_type_code VARCHAR(16) NOT NULL,
    sequence_value INT NOT NULL,
    released_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (organization_id, project_type_code, sequence_value),
    CONSTRAINT fk_released_project_type_org FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB;
INSERT INTO project_type_number_counter(organization_id,project_type_code,current_value)
SELECT o.id,t.code,COALESCE(MAX(CAST(RIGHT(p.code,5) AS UNSIGNED)),0)
FROM project_organization o CROSS JOIN project_type_definition t
LEFT JOIN project p ON p.organization_id=o.id AND p.parent_project_id IS NULL
    AND LEFT(p.code,CHAR_LENGTH(CONCAT(t.code,o.project_company_code)))=CONCAT(t.code,o.project_company_code)
    AND CHAR_LENGTH(p.code)=CHAR_LENGTH(CONCAT(t.code,o.project_company_code))+5
    AND RIGHT(p.code,5) REGEXP '^[0-9]{5}$'
GROUP BY o.id,t.code
ON DUPLICATE KEY UPDATE current_value=GREATEST(project_type_number_counter.current_value,VALUES(current_value));
