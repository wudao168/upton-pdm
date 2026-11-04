CREATE TABLE material_brand (
    id BINARY(16) NOT NULL PRIMARY KEY,
    name VARCHAR(160) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    previous_names JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    UNIQUE KEY ux_material_brand_name(name)
);
INSERT INTO material_brand(id,name,is_active,previous_names,row_version)
SELECT UUID_TO_BIN(UUID()),brands.name,1,JSON_ARRAY(),1 FROM (
    SELECT TRIM(brand) name FROM material_master WHERE brand IS NOT NULL AND TRIM(brand)<>''
    UNION SELECT TRIM(brand) name FROM engineering_kit WHERE brand IS NOT NULL AND TRIM(brand)<>''
) brands;
