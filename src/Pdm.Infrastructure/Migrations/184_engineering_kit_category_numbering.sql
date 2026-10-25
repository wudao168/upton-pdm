CREATE TABLE standard_library_category_counter (
    counter_id INT NOT NULL PRIMARY KEY,
    current_sequence BIGINT NOT NULL
);

-- 按现有一级分类顺序一次性分配编号；下级分类不单独编号，历史套件型号不改写。
UPDATE standard_library_category category
JOIN (
    SELECT id, ROW_NUMBER() OVER (ORDER BY sort_order,name,id) category_number
    FROM standard_library_category
    WHERE parent_id IS NULL AND id<>UUID_TO_BIN('53544c42-0000-0000-0000-000000000001') AND name<>'未分类'
) numbered ON numbered.id=category.id
SET category.code=CONCAT('UTJ-',IF(numbered.category_number<10,'0',''),numbered.category_number),
    category.row_version=category.row_version+1;

INSERT INTO standard_library_category_counter(counter_id,current_sequence)
SELECT 1,COUNT(*) FROM standard_library_category
WHERE parent_id IS NULL AND id<>UUID_TO_BIN('53544c42-0000-0000-0000-000000000001') AND name<>'未分类';

CREATE TABLE engineering_kit_model_counter (
    category_code VARCHAR(32) NOT NULL PRIMARY KEY,
    current_sequence BIGINT NOT NULL
);

INSERT INTO engineering_kit_model_counter(category_code,current_sequence)
SELECT SUBSTRING_INDEX(kit_model,'-',2), MAX(CAST(SUBSTRING_INDEX(kit_model,'-',-1) AS UNSIGNED))
FROM engineering_kit WHERE kit_model REGEXP '^UTJ-[0-9]{2,}-[0-9]{4}$'
GROUP BY SUBSTRING_INDEX(kit_model,'-',2);
