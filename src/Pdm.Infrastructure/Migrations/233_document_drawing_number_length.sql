ALTER TABLE document
    MODIFY COLUMN drawing_number VARCHAR(255) NOT NULL;

ALTER TABLE bom_item
    MODIFY COLUMN drawing_number VARCHAR(255) NOT NULL,
    MODIFY COLUMN parent_drawing_number VARCHAR(255) NULL;

ALTER TABLE drawing_review_item
    MODIFY COLUMN drawing_number VARCHAR(255) NOT NULL;
