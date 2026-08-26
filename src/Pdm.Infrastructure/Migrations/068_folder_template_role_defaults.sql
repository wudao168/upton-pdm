INSERT IGNORE INTO folder_template_permission(id,folder_key,principal_type,principal_key,access_mask)
SELECT UUID_TO_BIN(UUID()),defaults.folder_key,'Role',defaults.role_code,defaults.access_mask
FROM (
    SELECT 'mechanical' folder_key,'Engineer' role_code,15 access_mask UNION ALL
    SELECT 'mechanical','MechanicalManager',15 UNION ALL
    SELECT 'mechanical','TechnicalAssistant',15 UNION ALL
    SELECT 'mechanical','ProcessReviewer',3 UNION ALL
    SELECT 'mechanical','ProjectManager',3 UNION ALL
    SELECT 'mechanical','BusinessUnitManager',3 UNION ALL
    SELECT 'mechanical.air-sequence','CommissioningEngineer',15 UNION ALL
    SELECT 'electrical','ElectricalEngineer',15 UNION ALL
    SELECT 'electrical','HardwareEngineer',15 UNION ALL
    SELECT 'electrical','ProcessReviewer',3 UNION ALL
    SELECT 'electrical','ProjectManager',3 UNION ALL
    SELECT 'electrical','ElectricalSupervisor',3 UNION ALL
    SELECT 'electrical','BusinessUnitManager',3 UNION ALL
    SELECT 'purchase','ProcurementSpecialist',15 UNION ALL
    SELECT 'purchase','ProcurementManager',15 UNION ALL
    SELECT 'purchase','SupplyChain',3 UNION ALL
    SELECT 'purchase','ProjectManager',3 UNION ALL
    SELECT 'purchase','BusinessUnitManager',3 UNION ALL
    SELECT 'production','ProductionManager',15 UNION ALL
    SELECT 'production','ProductionAssistant',15 UNION ALL
    SELECT 'production','SupplyChain',3 UNION ALL
    SELECT 'production','ProductionViewer',3 UNION ALL
    SELECT 'production','MachiningSupervisor',3 UNION ALL
    SELECT 'production','MachiningOperator',3 UNION ALL
    SELECT 'production','AssemblySupervisor',3 UNION ALL
    SELECT 'production','AssemblyFitter',3 UNION ALL
    SELECT 'production','ElectricalSupervisor',3 UNION ALL
    SELECT 'production','AssemblyElectrician',3 UNION ALL
    SELECT 'production','ProjectManager',3 UNION ALL
    SELECT 'production','BusinessUnitManager',3 UNION ALL
    SELECT 'project-files','ProjectManager',15 UNION ALL
    SELECT 'project-files','TechnicalAssistant',15 UNION ALL
    SELECT 'project-files','BusinessUnitManager',3 UNION ALL
    SELECT 'presales','ProjectManager',15 UNION ALL
    SELECT 'presales','TechnicalAssistant',15 UNION ALL
    SELECT 'presales','BusinessUnitManager',3 UNION ALL
    SELECT 'customer-files','ProjectManager',15 UNION ALL
    SELECT 'customer-files','TechnicalAssistant',15 UNION ALL
    SELECT 'customer-files','BusinessUnitManager',3 UNION ALL
    SELECT 'acceptance','ProjectManager',15 UNION ALL
    SELECT 'acceptance','TechnicalAssistant',15 UNION ALL
    SELECT 'acceptance','BusinessUnitManager',3 UNION ALL
    SELECT 'media','ProjectManager',15 UNION ALL
    SELECT 'media','TechnicalAssistant',15 UNION ALL
    SELECT 'media','BusinessUnitManager',3 UNION ALL
    SELECT 'minutes','ProjectManager',15 UNION ALL
    SELECT 'minutes','TechnicalAssistant',15 UNION ALL
    SELECT 'minutes','BusinessUnitManager',3 UNION ALL
    SELECT 'mechanical.release','Engineer',3 UNION ALL
    SELECT 'mechanical.release','MechanicalManager',3 UNION ALL
    SELECT 'mechanical.release','TechnicalAssistant',3 UNION ALL
    SELECT 'mechanical.release','ProcessReviewer',3 UNION ALL
    SELECT 'mechanical.release','ProjectManager',3 UNION ALL
    SELECT 'mechanical.release','SupplyChain',3 UNION ALL
    SELECT 'mechanical.release','ProductionManager',3 UNION ALL
    SELECT 'mechanical.release','ProductionAssistant',3 UNION ALL
    SELECT 'mechanical.release','MachiningSupervisor',3 UNION ALL
    SELECT 'mechanical.release','MachiningOperator',3 UNION ALL
    SELECT 'mechanical.release','AssemblySupervisor',3 UNION ALL
    SELECT 'mechanical.release','AssemblyFitter',3 UNION ALL
    SELECT 'electrical.release','ElectricalEngineer',3 UNION ALL
    SELECT 'electrical.release','HardwareEngineer',3 UNION ALL
    SELECT 'electrical.release','ProcessReviewer',3 UNION ALL
    SELECT 'electrical.release','ProjectManager',3 UNION ALL
    SELECT 'electrical.release','SupplyChain',3 UNION ALL
    SELECT 'electrical.release','ProductionManager',3 UNION ALL
    SELECT 'electrical.release','ProductionAssistant',3 UNION ALL
    SELECT 'electrical.release','ElectricalSupervisor',3 UNION ALL
    SELECT 'electrical.release','AssemblyElectrician',3
) defaults
INNER JOIN folder_template_node node ON node.folder_key=defaults.folder_key
INNER JOIN role_definition role ON role.role_code=defaults.role_code;
