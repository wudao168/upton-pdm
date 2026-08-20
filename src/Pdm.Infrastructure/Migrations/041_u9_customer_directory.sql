UPDATE pdm_customer
SET source_system = 'u9c'
WHERE source_system = 'crm';

UPDATE crm_integration_setting
SET password_ciphertext = ''
WHERE id = 1;
