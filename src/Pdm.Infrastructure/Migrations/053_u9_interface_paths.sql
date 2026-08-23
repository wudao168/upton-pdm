ALTER TABLE u9_material_integration_setting
    ADD COLUMN customer_query_path VARCHAR(500) NOT NULL DEFAULT '/webapi/GetCommonReference/Create' AFTER item_delete_path,
    ADD COLUMN bom_create_path VARCHAR(500) NOT NULL DEFAULT '/webapi/BOM/Create' AFTER customer_query_path,
    ADD COLUMN bom_query_path VARCHAR(500) NOT NULL DEFAULT '/webapi/BOM/Query' AFTER bom_create_path,
    ADD COLUMN bom_modify_path VARCHAR(500) NOT NULL DEFAULT '/webapi/BOM/Modify' AFTER bom_query_path,
    ADD COLUMN bom_delete_path VARCHAR(500) NOT NULL DEFAULT '/webapi/BOM/Delete' AFTER bom_modify_path,
    ADD COLUMN bom_batch_unapprove_path VARCHAR(500) NOT NULL DEFAULT '/webapi/BOM/BatchUnApprove' AFTER bom_delete_path,
    ADD COLUMN bom_bip_query_page_path VARCHAR(500) NOT NULL DEFAULT '/webapi/BOM/BIPQueryPage' AFTER bom_batch_unapprove_path;
