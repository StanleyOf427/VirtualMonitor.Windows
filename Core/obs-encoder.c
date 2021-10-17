#include "obs.h"
#include "obs-internal.h"
#include "util/util_uint64.h"

#define encoder_active(encoder) os_atomic_load_bool(&encoder->active)
#define set_encoder_active(encoder, val) \
	os_atomic_set_bool(&encoder->active, val)

static bool init_encoder(struct obs_encoder* encoder, const char* name,
	obs_data_t* settings, obs_data_t* hotkey_data)
{
	pthread_mutexattr_t attr;

	pthread_mutex_init_value(&encoder->init_mutex);
	pthread_mutex_init_value(&encoder->callbacks_mutex);
	pthread_mutex_init_value(&encoder->outputs_mutex);
	pthread_mutex_init_value(&encoder->pause.mutex);

	if (pthread_mutexattr_init(&attr) != 0)
		return false;
	if (pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE) != 0)
		return false;
	if (!obs_context_data_init(&encoder->context, OBS_OBJ_TYPE_ENCODER,
		settings, name, hotkey_data, false))
		return false;
	if (pthread_mutex_init(&encoder->init_mutex, &attr) != 0)
		return false;
	if (pthread_mutex_init(&encoder->callbacks_mutex, &attr) != 0)
		return false;
	if (pthread_mutex_init(&encoder->outputs_mutex, NULL) != 0)
		return false;
	if (pthread_mutex_init(&encoder->pause.mutex, NULL) != 0)
		return false;

	if (encoder->orig_info.get_defaults) {
		encoder->orig_info.get_defaults(encoder->context.settings);
	}
	if (encoder->orig_info.get_defaults2) {
		encoder->orig_info.get_defaults2(encoder->context.settings,
			encoder->orig_info.type_data);
	}

	return true;
}

static struct obs_encoder*
create_encoder(const char* id, enum obs_encoder_type type, const char* name,
	obs_data_t* settings, size_t mixer_idx, obs_data_t* hotkey_data)
{
	struct obs_encoder* encoder;
	struct obs_encoder_info* ei = find_encoder(id);
	bool success;

	if (ei && ei->type != type)
		return NULL;

	encoder = bzalloc(sizeof(struct obs_encoder));
	encoder->mixer_idx = mixer_idx;

	if (!ei) {
		blog(LOG_ERROR, "Encoder ID '%s' not found", id);

		encoder->info.id = bstrdup(id);
		encoder->info.type = type;
		encoder->owns_info_id = true;
		encoder->orig_info = encoder->info;
	}
	else {
		encoder->info = *ei;
		encoder->orig_info = *ei;
	}

	success = init_encoder(encoder, name, settings, hotkey_data);
	if (!success) {
		blog(LOG_ERROR, "creating encoder '%s' (%s) failed", name, id);
		obs_encoder_destroy(encoder);
		return NULL;
	}

	encoder->control = bzalloc(sizeof(obs_weak_encoder_t));
	encoder->control->encoder = encoder;

	obs_context_data_insert(&encoder->context, &obs->data.encoders_mutex,
		&obs->data.first_encoder);

	blog(LOG_DEBUG, "encoder '%s' (%s) created", name, id);
	return encoder;
}


obs_encoder_t* obs_video_encoder_create(const char* id, const char* name,
	obs_data_t* settings,
	obs_data_t* hotkey_data)
{
	if (!name || !id)
		return NULL;
	return create_encoder(id, OBS_ENCODER_VIDEO, name, settings, 0,
		hotkey_data);
}

static void obs_encoder_actually_destroy(obs_encoder_t* encoder)
{
	if (encoder) {
		pthread_mutex_lock(&encoder->outputs_mutex);
		for (size_t i = 0; i < encoder->outputs.num; i++) {
			struct obs_output* output = encoder->outputs.array[i];
			obs_output_remove_encoder(output, encoder);
		}
		da_free(encoder->outputs);
		pthread_mutex_unlock(&encoder->outputs_mutex);

		blog(LOG_DEBUG, "encoder '%s' destroyed",
			encoder->context.name);

		free_audio_buffers(encoder);

		if (encoder->context.data)
			encoder->info.destroy(encoder->context.data);
		da_free(encoder->callbacks);
		pthread_mutex_destroy(&encoder->init_mutex);
		pthread_mutex_destroy(&encoder->callbacks_mutex);
		pthread_mutex_destroy(&encoder->outputs_mutex);
		pthread_mutex_destroy(&encoder->pause.mutex);
		obs_context_data_free(&encoder->context);
		if (encoder->owns_info_id)
			bfree((void*)encoder->info.id);
		if (encoder->last_error_message)
			bfree(encoder->last_error_message);
		bfree(encoder);
	}
}


/* does not actually destroy the encoder until all connections to it have been
 * removed. (full reference counting really would have been superfluous) */
void obs_encoder_destroy(obs_encoder_t* encoder)
{
	if (encoder) {
		bool destroy;

		obs_context_data_remove(&encoder->context);

		pthread_mutex_lock(&encoder->init_mutex);
		pthread_mutex_lock(&encoder->callbacks_mutex);
		destroy = encoder->callbacks.num == 0;
		if (!destroy)
			encoder->destroy_on_stop = true;
		pthread_mutex_unlock(&encoder->callbacks_mutex);
		pthread_mutex_unlock(&encoder->init_mutex);

		if (destroy)
			obs_encoder_actually_destroy(encoder);
	}
}


void obs_encoder_update(obs_encoder_t* encoder, obs_data_t* settings)
{
	if (!obs_encoder_valid(encoder, "obs_encoder_update"))
		return;

	obs_data_apply(encoder->context.settings, settings);

	if (encoder->info.update && encoder->context.data)
		encoder->info.update(encoder->context.data,
			encoder->context.settings);
}


static inline bool obs_encoder_initialize_internal(obs_encoder_t* encoder)
{
	if (encoder_active(encoder))
		return true;
	if (encoder->initialized)
		return true;

	obs_encoder_shutdown(encoder);

	if (encoder->orig_info.create) {
		can_reroute = true;
		encoder->info = encoder->orig_info;
		encoder->context.data = encoder->orig_info.create(
			encoder->context.settings, encoder);
		can_reroute = false;
	}
	if (!encoder->context.data)
		return false;

	if (encoder->orig_info.type == OBS_ENCODER_AUDIO)
		intitialize_audio_encoder(encoder);

	encoder->initialized = true;
	return true;
}

bool obs_encoder_initialize(obs_encoder_t* encoder)
{
	bool success;

	if (!encoder)
		return false;

	pthread_mutex_lock(&encoder->init_mutex);
	success = obs_encoder_initialize_internal(encoder);
	pthread_mutex_unlock(&encoder->init_mutex);

	return success;
}

void obs_encoder_shutdown(obs_encoder_t* encoder)
{
	pthread_mutex_lock(&encoder->init_mutex);
	if (encoder->context.data) {
		encoder->info.destroy(encoder->context.data);
		encoder->context.data = NULL;
		encoder->paired_encoder = NULL;
		encoder->first_received = false;
		encoder->offset_usec = 0;
		encoder->start_ts = 0;
	}
	obs_encoder_set_last_error(encoder, NULL);
	pthread_mutex_unlock(&encoder->init_mutex);
}


static inline void obs_encoder_start_internal(
	obs_encoder_t* encoder,
	void (*new_packet)(void* param, struct encoder_packet* packet),
	void* param)
{
	struct encoder_callback cb = { false, new_packet, param };
	bool first = false;

	if (!encoder->context.data)
		return;

	pthread_mutex_lock(&encoder->callbacks_mutex);

	first = (encoder->callbacks.num == 0);

	size_t idx = get_callback_idx(encoder, new_packet, param);
	if (idx == DARRAY_INVALID)
		da_push_back(encoder->callbacks, &cb);

	pthread_mutex_unlock(&encoder->callbacks_mutex);

	if (first) {
		os_atomic_set_bool(&encoder->paused, false);
		pause_reset(&encoder->pause);

		encoder->cur_pts = 0;
		add_connection(encoder);
	}
}

void obs_encoder_start(obs_encoder_t* encoder,
	void (*new_packet)(void* param,
		struct encoder_packet* packet),
	void* param)
{
	if (!obs_encoder_valid(encoder, "obs_encoder_start"))
		return;
	if (!obs_ptr_valid(new_packet, "obs_encoder_start"))
		return;

	pthread_mutex_lock(&encoder->init_mutex);
	obs_encoder_start_internal(encoder, new_packet, param);
	pthread_mutex_unlock(&encoder->init_mutex);
}

static inline bool obs_encoder_stop_internal(
	obs_encoder_t* encoder,
	void (*new_packet)(void* param, struct encoder_packet* packet),
	void* param)
{
	bool last = false;
	size_t idx;

	pthread_mutex_lock(&encoder->callbacks_mutex);

	idx = get_callback_idx(encoder, new_packet, param);
	if (idx != DARRAY_INVALID) {
		da_erase(encoder->callbacks, idx);
		last = (encoder->callbacks.num == 0);
	}

	pthread_mutex_unlock(&encoder->callbacks_mutex);

	if (last) {
		remove_connection(encoder, true);
		encoder->initialized = false;

		if (encoder->destroy_on_stop) {
			pthread_mutex_unlock(&encoder->init_mutex);
			obs_encoder_actually_destroy(encoder);
			return true;
		}
	}

	return false;
}

void obs_encoder_stop(obs_encoder_t* encoder,
	void (*new_packet)(void* param,
		struct encoder_packet* packet),
	void* param)
{
	bool destroyed;

	if (!obs_encoder_valid(encoder, "obs_encoder_stop"))
		return;
	if (!obs_ptr_valid(new_packet, "obs_encoder_stop"))
		return;

	pthread_mutex_lock(&encoder->init_mutex);
	destroyed = obs_encoder_stop_internal(encoder, new_packet, param);
	if (!destroyed)
		pthread_mutex_unlock(&encoder->init_mutex);
}

void obs_encoder_set_video(obs_encoder_t* encoder, video_t* video)
{
	const struct video_output_info* voi;

	if (!obs_encoder_valid(encoder, "obs_encoder_set_video"))
		return;
	if (encoder->info.type != OBS_ENCODER_VIDEO) {
		blog(LOG_WARNING,
			"obs_encoder_set_video: "
			"encoder '%s' is not a video encoder",
			obs_encoder_get_name(encoder));
		return;
	}
	if (!video)
		return;

	voi = video_output_get_info(video);

	encoder->media = video;
	encoder->timebase_num = voi->fps_den;
	encoder->timebase_den = voi->fps_num;
}

video_t* obs_encoder_video(const obs_encoder_t* encoder)
{
	if (!obs_encoder_valid(encoder, "obs_encoder_video"))
		return NULL;
	if (encoder->info.type != OBS_ENCODER_VIDEO) {
		blog(LOG_WARNING,
			"obs_encoder_set_video: "
			"encoder '%s' is not a video encoder",
			obs_encoder_get_name(encoder));
		return NULL;
	}

	return encoder->media;
}

bool obs_encoder_active(const obs_encoder_t* encoder)
{
	return obs_encoder_valid(encoder, "obs_encoder_active")
		? encoder_active(encoder)
		: false;
}



static void send_first_video_packet(struct obs_encoder* encoder,
	struct encoder_callback* cb,
	struct encoder_packet* packet)
{
	struct encoder_packet first_packet;
	DARRAY(uint8_t) data;
	uint8_t* sei;
	size_t size;

	/* always wait for first keyframe */
	if (!packet->keyframe)
		return;

	da_init(data);

	if (!get_sei(encoder, &sei, &size) || !sei || !size) {
		cb->new_packet(cb->param, packet);
		cb->sent_first_packet = true;
		return;
	}

	da_push_back_array(data, sei, size);
	da_push_back_array(data, packet->data, packet->size);

	first_packet = *packet;
	first_packet.data = data.array;
	first_packet.size = data.num;

	cb->new_packet(cb->param, &first_packet);
	cb->sent_first_packet = true;

	da_free(data);
}

bool do_encode(struct obs_encoder* encoder, struct encoder_frame* frame)
{
	profile_start(do_encode_name);
	if (!encoder->profile_encoder_encode_name)
		encoder->profile_encoder_encode_name =
		profile_store_name(obs_get_profiler_name_store(),
			"encode(%s)", encoder->context.name);

	struct encoder_packet pkt = { 0 };
	bool received = false;
	bool success;

	pkt.timebase_num = encoder->timebase_num;
	pkt.timebase_den = encoder->timebase_den;
	pkt.encoder = encoder;

	profile_start(encoder->profile_encoder_encode_name);
	success = encoder->info.encode(encoder->context.data, frame, &pkt,
		&received);
	profile_end(encoder->profile_encoder_encode_name);
	send_off_encoder_packet(encoder, success, received, &pkt);

	profile_end(do_encode_name);

	return success;
}



static inline void send_packet(struct obs_encoder *encoder,
	struct encoder_callback* cb,
	struct encoder_packet* packet)
{
	/* include SEI in first video packet */
	if (encoder->info.type == OBS_ENCODER_VIDEO && !cb->sent_first_packet)
		send_first_video_packet(encoder, cb, packet);
	else
		cb->new_packet(cb->param, packet);
}


void obs_encoder_add_output(struct obs_encoder* encoder,
	struct obs_output* output)
{
	if (!encoder)
		return;

	pthread_mutex_lock(&encoder->outputs_mutex);
	da_push_back(encoder->outputs, &output);
	pthread_mutex_unlock(&encoder->outputs_mutex);
}

void obs_encoder_packet_create_instance(struct encoder_packet* dst,
	const struct encoder_packet* src)
{
	long* p_refs;

	*dst = *src;
	p_refs = bmalloc(src->size + sizeof(long));
	dst->data = (void*)(p_refs + 1);
	*p_refs = 1;
	memcpy(dst->data, src->data, src->size);
}


/* OBS_DEPRECATED */
void obs_duplicate_encoder_packet(struct encoder_packet* dst,
	const struct encoder_packet* src)
{
	obs_encoder_packet_create_instance(dst, src);
}

/* OBS_DEPRECATED */
void obs_free_encoder_packet(struct encoder_packet* packet)
{
	obs_encoder_packet_release(packet);
}

void obs_encoder_packet_ref(struct encoder_packet* dst,
	struct encoder_packet* src)
{
	if (!src)
		return;

	if (src->data) {
		long* p_refs = ((long*)src->data) - 1;
		os_atomic_inc_long(p_refs);
	}

	*dst = *src;
}

void obs_encoder_packet_release(struct encoder_packet* pkt)
{
	if (!pkt)
		return;

	if (pkt->data) {
		long* p_refs = ((long*)pkt->data) - 1;
		if (os_atomic_dec_long(p_refs) == 0)
			bfree(p_refs);
	}

	memset(pkt, 0, sizeof(struct encoder_packet));
}

void obs_encoder_set_preferred_video_format(obs_encoder_t* encoder,
	enum video_format format)
{
	if (!encoder || encoder->info.type != OBS_ENCODER_VIDEO)
		return;

	encoder->preferred_format = format;
}
