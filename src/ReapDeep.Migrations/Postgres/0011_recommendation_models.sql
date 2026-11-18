ALTER TABLE item_recommendations DROP CONSTRAINT item_recommendations_model_check;

ALTER TABLE item_recommendations ADD CONSTRAINT item_recommendations_model_check
    CHECK (model IN ('popular', 'trending', 'co_viewed', 'bought_together', 'co_viewed_weighted', 'co_viewed_session'));
