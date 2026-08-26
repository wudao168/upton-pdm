UPDATE organization_unit
SET can_manufacture = 1
WHERE parent_unit_id IS NULL
  AND can_manufacture = 0;
