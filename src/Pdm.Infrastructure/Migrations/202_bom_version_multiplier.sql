ALTER TABLE bom_version
    ADD COLUMN whole_set_multiplier INT NOT NULL DEFAULT 1,
    ADD COLUMN is_multiplier_overridden TINYINT(1) NOT NULL DEFAULT 0;

-- 旧发布包继续使用各自冻结的倍率；BOM工作属性从最近一次正式发布继承。
UPDATE bom_version version
JOIN (
    SELECT binding.version_id, binding.whole_set_multiplier,
           ROW_NUMBER() OVER (PARTITION BY binding.version_id ORDER BY binding.published_at DESC, binding.id DESC) AS position
    FROM (
        SELECT id, standard_bom_version_id AS version_id, whole_set_multiplier, published_at
        FROM release_package WHERE state='Published' AND release_scope IN ('StandardFormal','StandardSupplement','LegacyCombined')
        UNION ALL
        SELECT id, non_standard_bom_version_id, whole_set_multiplier, published_at
        FROM release_package WHERE state='Published' AND release_scope IN ('NonStandardWithDrawing','NonStandardSupplement','LegacyCombined')
        UNION ALL
        SELECT id, electrical_bom_version_id, whole_set_multiplier, published_at
        FROM release_package WHERE state='Published' AND release_scope IN ('ElectricalFormal','ElectricalSupplement','LegacyCombined')
    ) binding WHERE binding.version_id IS NOT NULL
) published ON published.version_id=version.id AND published.position=1
SET version.whole_set_multiplier=GREATEST(1,published.whole_set_multiplier),
    version.is_multiplier_overridden=(published.whole_set_multiplier<>1);

UPDATE bom_version draft JOIN bom_version base ON base.id=draft.base_version_id
SET draft.whole_set_multiplier=base.whole_set_multiplier,
    draft.is_multiplier_overridden=base.is_multiplier_overridden WHERE draft.state='Draft';
