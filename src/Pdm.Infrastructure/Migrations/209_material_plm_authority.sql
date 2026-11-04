-- PLM保留研发主数据，U9C回读仅记录对账结果；不批量改写历史业务数据。
ALTER TABLE material_master
    ADD COLUMN u9_reconciliation_message TEXT NULL,
    ADD COLUMN u9_reconciled_at DATETIME(6) NULL,
    ADD COLUMN u9_is_effective TINYINT(1) NULL;
