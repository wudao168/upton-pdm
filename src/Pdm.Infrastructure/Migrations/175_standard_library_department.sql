ALTER TABLE organization_unit ADD COLUMN can_archive_standard_library TINYINT(1) NOT NULL DEFAULT 0;
UPDATE organization_unit SET can_archive_standard_library=1 WHERE name LIKE '%标准化%';
