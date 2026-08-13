CREATE TABLE IF NOT EXISTS project_organization (
    id BINARY(16) NOT NULL PRIMARY KEY,
    name VARCHAR(200) NOT NULL,
    project_company_code CHAR(1) NOT NULL,
    model_company_code VARCHAR(8) NOT NULL,
    crm_company_name VARCHAR(200) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    UNIQUE KEY ux_project_organization_name (name),
    UNIQUE KEY ux_project_organization_project_code (project_company_code),
    UNIQUE KEY ux_project_organization_model_code (model_company_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project_type_definition (
    code CHAR(1) NOT NULL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS equipment_type_definition (
    code TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    CONSTRAINT ck_equipment_type_code CHECK (code BETWEEN 0 AND 99)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO project_organization(id,name,project_company_code,model_company_code,crm_company_name,is_active) VALUES
(UNHEX('70000000000000000000000000000001'),'昆山阿普顿自动化系统有限公司','7','AK','昆山阿普顿自动化系统有限公司',1),
(UNHEX('30000000000000000000000000000001'),'广州阿普顿自动化系统有限公司','3','AG','广州阿普顿自动化系统有限公司',1),
(UNHEX('90000000000000000000000000000001'),'南京阿普顿自动化系统有限公司','9','AN','南京阿普顿自动化系统有限公司',1);

INSERT IGNORE INTO project_type_definition(code,name,is_active) VALUES
('P','标准项目',1),('W','外发项目',1),('R','研发项目',1),('S','售后项目',1);

INSERT IGNORE INTO equipment_type_definition(code,name,is_active) VALUES
(0,'类型00',1),(1,'类型01',1),(2,'类型02',1),(3,'类型03',1),(4,'类型04',1),(5,'类型05',1),(6,'类型06',1),(7,'类型07',1),(8,'类型08',1),(9,'类型09',1),
(10,'类型10',1),(11,'类型11',1),(12,'类型12',1),(13,'类型13',1),(14,'类型14',1),(15,'类型15',1),(16,'类型16',1),(17,'类型17',1),(18,'类型18',1),(19,'类型19',1),
(20,'类型20',1),(21,'类型21',1),(22,'类型22',1),(23,'类型23',1),(24,'类型24',1),(25,'类型25',1),(26,'类型26',1),(27,'类型27',1),(28,'类型28',1),(29,'类型29',1),
(30,'类型30',1),(31,'类型31',1),(32,'类型32',1),(33,'类型33',1),(34,'类型34',1),(35,'类型35',1),(36,'类型36',1),(37,'类型37',1),(38,'类型38',1),(39,'类型39',1),
(40,'类型40',1),(41,'类型41',1),(42,'类型42',1),(43,'类型43',1),(44,'类型44',1),(45,'类型45',1),(46,'类型46',1),(47,'类型47',1),(48,'类型48',1),(49,'类型49',1),
(50,'类型50',1),(51,'类型51',1),(52,'类型52',1),(53,'类型53',1),(54,'类型54',1),(55,'类型55',1),(56,'类型56',1),(57,'类型57',1),(58,'类型58',1),(59,'类型59',1),
(60,'类型60',1),(61,'类型61',1),(62,'类型62',1),(63,'类型63',1),(64,'类型64',1),(65,'类型65',1),(66,'类型66',1),(67,'类型67',1),(68,'类型68',1),(69,'类型69',1),
(70,'类型70',1),(71,'类型71',1),(72,'类型72',1),(73,'类型73',1),(74,'类型74',1),(75,'类型75',1),(76,'类型76',1),(77,'类型77',1),(78,'类型78',1),(79,'类型79',1),
(80,'类型80',1),(81,'类型81',1),(82,'类型82',1),(83,'类型83',1),(84,'类型84',1),(85,'类型85',1),(86,'类型86',1),(87,'类型87',1),(88,'类型88',1),(89,'类型89',1),
(90,'类型90',1),(91,'类型91',1),(92,'类型92',1),(93,'类型93',1),(94,'类型94',1),(95,'类型95',1),(96,'类型96',1),(97,'类型97',1),(98,'类型98',1),(99,'类型99',1);

ALTER TABLE project
    ADD COLUMN project_alias VARCHAR(200) NULL AFTER name,
    ADD COLUMN organization_id BINARY(16) NULL AFTER project_alias,
    ADD COLUMN project_type_code CHAR(1) NULL AFTER organization_id,
    ADD COLUMN equipment_type_code TINYINT UNSIGNED NULL AFTER project_type_code,
    ADD COLUMN customer_code VARCHAR(30) NULL AFTER equipment_type_code,
    ADD COLUMN customer_name VARCHAR(200) NULL AFTER customer_code,
    ADD COLUMN customer_project_sequence SMALLINT UNSIGNED NULL AFTER customer_name,
    ADD COLUMN device_model VARCHAR(160) NULL AFTER customer_project_sequence,
    ADD COLUMN signed_date DATE NULL AFTER device_model,
    ADD COLUMN quantity INT UNSIGNED NOT NULL DEFAULT 1 AFTER signed_date,
    ADD COLUMN parent_project_id BINARY(16) NULL AFTER quantity,
    ADD COLUMN child_sequence SMALLINT UNSIGNED NULL AFTER parent_project_id,
    ADD CONSTRAINT fk_project_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id),
    ADD CONSTRAINT fk_project_parent FOREIGN KEY (parent_project_id) REFERENCES project(id),
    ADD UNIQUE KEY ux_project_parent_child (parent_project_id, child_sequence),
    ADD KEY ix_project_customer (organization_id, customer_code);

CREATE TABLE IF NOT EXISTS project_number_counter (
    organization_id BINARY(16) NOT NULL PRIMARY KEY,
    current_value INT UNSIGNED NOT NULL,
    CONSTRAINT fk_project_number_counter_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS serial_number_counter (
    organization_id BINARY(16) NOT NULL PRIMARY KEY,
    current_value INT UNSIGNED NOT NULL,
    CONSTRAINT fk_serial_number_counter_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS customer_project_counter (
    organization_id BINARY(16) NOT NULL,
    customer_code VARCHAR(30) NOT NULL,
    current_value SMALLINT UNSIGNED NOT NULL,
    PRIMARY KEY (organization_id, customer_code),
    CONSTRAINT fk_customer_project_counter_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project_serial_number (
    project_id BINARY(16) NOT NULL,
    sequence_no INT UNSIGNED NOT NULL,
    serial_number CHAR(8) NOT NULL,
    PRIMARY KEY (project_id, sequence_no),
    UNIQUE KEY ux_project_serial_number (serial_number),
    CONSTRAINT fk_project_serial_number_project FOREIGN KEY (project_id) REFERENCES project(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO project_number_counter(organization_id,current_value)
SELECT id,0 FROM project_organization;

UPDATE project_number_counter counter
INNER JOIN project_organization organization ON organization.id=counter.organization_id
SET counter.current_value=(
    SELECT COALESCE(MAX(CAST(SUBSTRING(project.code,3,5) AS UNSIGNED)),0)
    FROM project
    WHERE project.parent_project_id IS NULL
      AND project.code REGEXP CONCAT('^[PWRS]',organization.project_company_code,'[0-9]{5}$')
);

INSERT IGNORE INTO serial_number_counter(organization_id,current_value)
SELECT id,0 FROM project_organization;
