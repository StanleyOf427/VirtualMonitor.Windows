#include "obs.h"
#include "obs-internal.h"

void obs_source_release(obs_source_t* source)
{
	if (!obs) {
		blog(LOG_WARNING, "Tried to release a source when the OBS "
			"core is shut down!");
		return;
	}

	if (!source)
		return;

	obs_weak_source_t* control = source->control;
	if (obs_ref_release(&control->ref)) {
		obs_source_destroy(source);
		obs_weak_source_release(control);
	}
}

void obs_source_video_tick(obs_source_t* source, float seconds)
{
	bool now_showing, now_active;

	if (!obs_source_valid(source, "obs_source_video_tick"))
		return;

	if (source->info.type == OBS_SOURCE_TYPE_TRANSITION)
		obs_transition_tick(source, seconds);

	if ((source->info.output_flags & OBS_SOURCE_ASYNC) != 0)
		async_tick(source);

	if (os_atomic_load_long(&source->defer_update_count) > 0)
		obs_source_deferred_update(source);

	/* reset the filter render texture information once every frame */
	if (source->filter_texrender)
		gs_texrender_reset(source->filter_texrender);

	/* call show/hide if the reference changed */
	now_showing = !!source->show_refs;
	if (now_showing != source->showing) {
		if (now_showing) {
			show_source(source);
		}
		else {
			hide_source(source);
		}

		if (source->filters.num) {
			for (size_t i = source->filters.num; i > 0; i--) {
				obs_source_t* filter =
					source->filters.array[i - 1];
				if (now_showing) {
					show_source(filter);
				}
				else {
					hide_source(filter);
				}
			}
		}

		source->showing = now_showing;
	}

	/* call activate/deactivate if the reference changed */
	now_active = !!source->activate_refs;
	if (now_active != source->active) {
		if (now_active) {
			activate_source(source);
		}
		else {
			deactivate_source(source);
		}

		if (source->filters.num) {
			for (size_t i = source->filters.num; i > 0; i--) {
				obs_source_t* filter =
					source->filters.array[i - 1];
				if (now_active) {
					activate_source(filter);
				}
				else {
					deactivate_source(filter);
				}
			}
		}

		source->active = now_active;
	}

	if (source->context.data && source->info.video_tick)
		source->info.video_tick(source->context.data, seconds);

	source->async_rendered = false;
	source->deinterlace_rendered = false;
}
