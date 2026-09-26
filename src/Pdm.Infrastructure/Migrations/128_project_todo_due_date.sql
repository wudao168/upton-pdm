SET @sql = IF(
    EXISTS(
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = DATABASE()
          AND table_name = 'user_notification'
          AND column_name = 'due_date'
    ),
    'SELECT 1',
    'ALTER TABLE user_notification ADD COLUMN due_date DATE NULL AFTER read_at'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
