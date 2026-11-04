ALTER TABLE bom_item ADD COLUMN is_summary_quantity TINYINT(1) NOT NULL DEFAULT 0;
UPDATE bom_item SET is_summary_quantity = 1 WHERE item_source = 'Summary';
