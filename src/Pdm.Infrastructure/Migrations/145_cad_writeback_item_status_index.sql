ALTER TABLE cad_property_writeback
    ADD INDEX ix_cad_writeback_item_document_status (bom_item_id, source_document_id, status);
